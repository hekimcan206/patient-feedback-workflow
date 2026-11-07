using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Options;
using PatientFeedback.Dispatch;
using PatientFeedback.Web.Infrastructure;

namespace PatientFeedback.Web.Dispatch;

/// <summary>A message as it arrived on the (simulated) patient's phone.</summary>
public sealed record HandsetMessage(long Sequence, Channel Channel, string Phone, string Text, string Link, DateTimeOffset DeliveredAt);

/// <summary>
/// The fake providers' delivery sink: stands in for real phones so the simulator, the end-to-end test and the load
/// test can "receive" survey links. Bounded ring buffer; exposed only under <c>/sim</c> when simulation is enabled.
/// </summary>
public sealed class FakeHandset
{
    private const int Capacity = 20_000;
    private readonly ConcurrentQueue<HandsetMessage> _messages = new();
    private long _sequence;

    public void Deliver(Channel channel, string phone, string text, string link, DateTimeOffset at)
    {
        _messages.Enqueue(new HandsetMessage(Interlocked.Increment(ref _sequence), channel, phone, text, link, at));
        while (_messages.Count > Capacity && _messages.TryDequeue(out _))
        {
        }
    }

    public IReadOnlyList<HandsetMessage> After(long sequence, int max) =>
        [.. _messages.Where(m => m.Sequence > sequence).Take(max)];
}

/// <summary>
/// Shared behaviour of the fake providers: latency, provider-side idempotency, deterministic failure numbers for
/// tests and demos, and random transient failures. The error codes mirror the real providers' categories so the
/// dispatcher's classification is exercised realistically.
/// </summary>
public abstract class FakeProviderBase(FakeHandset handset, IOptions<FakeProviderOptions> options, TimeProvider clock) : IChannelAdapter
{
    private readonly ConcurrentDictionary<string, string> _accepted = new(StringComparer.Ordinal);
    private readonly Random _random = options.Value.Seed is { } seed ? new Random(seed) : Random.Shared;
    private readonly Lock _randomLock = new();

    protected FakeProviderOptions Options => options.Value;

    public abstract Channel Channel { get; }

    public async Task<SendResult> SendAsync(OutboundMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        await Task.Delay(NextLatency(), cancellationToken);

        // Provider-side idempotency: a retry after an ambiguous timeout must not reach the patient twice.
        if (_accepted.TryGetValue(message.IdempotencyKey, out var previous))
        {
            return SendResult.Accepted(previous);
        }

        var result = Classify(message.Phone, Roll());
        if (result is not null)
        {
            return result;
        }

        var providerId = ProviderPrefix + Guid.NewGuid().ToString("N")[..20];
        _accepted[message.IdempotencyKey] = providerId;
        handset.Deliver(Channel, message.Phone, Render(message), message.Parameters["link"], clock.GetUtcNow());
        return SendResult.Accepted(providerId);
    }

    protected abstract string ProviderPrefix { get; }

    /// <summary>Returns a failure for this attempt, or null to accept.</summary>
    protected abstract SendResult? Classify(string phone, double roll);

    protected static string Suffix(string phone) => phone[^2..];

    private static string Render(OutboundMessage m) => string.Format(
        CultureInfo.InvariantCulture,
        "Geçmiş olsun. {0} ziyaretinizi bir dakikada değerlendirir misiniz? {1} Mesaj almak istemiyorsanız IPTAL yazın.",
        m.Parameters["department"],
        m.Parameters["link"]);

    private double Roll()
    {
        lock (_randomLock)
        {
            return _random.NextDouble();
        }
    }

    private TimeSpan NextLatency()
    {
        lock (_randomLock)
        {
            return TimeSpan.FromMilliseconds(_random.Next(Options.MinLatencyMs, Math.Max(Options.MinLatencyMs, Options.MaxLatencyMs) + 1));
        }
    }
}

/// <summary>
/// WhatsApp Business-style provider. Template messages (utility category) can be sent outside the 24-hour customer
/// service window, so the window itself is not a failure mode here.
/// <list type="bullet">
/// <item>Numbers ending in 13: recipient not on WhatsApp (permanent, code 131026) → the dispatcher falls back to SMS.</item>
/// <item>Numbers ending in 14: the user blocked the business (treated as opt-out on this channel).</item>
/// <item>Random: throttling (code 130429, Retry-After 1 s) and timeouts.</item>
/// </list>
/// </summary>
public sealed class FakeWhatsAppProvider(FakeHandset handset, IOptions<FakeProviderOptions> options, TimeProvider clock)
    : FakeProviderBase(handset, options, clock)
{
    public override Channel Channel => Channel.WhatsApp;

    protected override string ProviderPrefix => "wamid.";

    protected override SendResult? Classify(string phone, double roll) => Suffix(phone) switch
    {
        "13" => SendResult.Permanent("131026-undeliverable"),
        "14" => SendResult.OptedOut("131050-user-blocked"),
        _ when roll < Options.WhatsAppRateLimitRate => SendResult.Transient("130429-rate-limit", TimeSpan.FromSeconds(1)),
        _ when roll < Options.WhatsAppRateLimitRate + Options.WhatsAppTimeoutRate => SendResult.Transient("timeout"),
        _ => null,
    };
}

/// <summary>
/// SMS gateway-style provider.
/// <list type="bullet">
/// <item>Numbers ending in 77: invalid / unallocated number (permanent).</item>
/// <item>Numbers ending in 78: on the operator's STOP list (opt-out).</item>
/// <item>Random: 503 from the gateway (transient).</item>
/// </list>
/// </summary>
public sealed class FakeSmsProvider(FakeHandset handset, IOptions<FakeProviderOptions> options, TimeProvider clock)
    : FakeProviderBase(handset, options, clock)
{
    public override Channel Channel => Channel.Sms;

    protected override string ProviderPrefix => "sms-";

    protected override SendResult? Classify(string phone, double roll) => Suffix(phone) switch
    {
        "77" => SendResult.Permanent("invalid-number"),
        "78" => SendResult.OptedOut("operator-stop-list"),
        _ when roll < Options.SmsServerErrorRate => SendResult.Transient("503-gateway"),
        _ => null,
    };
}
