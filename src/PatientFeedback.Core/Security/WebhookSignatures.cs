using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PatientFeedback.Security;

/// <summary>A symmetric signing secret in Standard Webhooks format: <c>whsec_</c> + base64 of 24–64 random bytes.</summary>
public sealed class WebhookSecret
{
    public const string Prefix = "whsec_";

    private readonly byte[] _key;

    private WebhookSecret(byte[] key) => _key = key;

    internal ReadOnlySpan<byte> Key => _key;

    public static WebhookSecret Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var encoded = value.StartsWith(Prefix, StringComparison.Ordinal) ? value[Prefix.Length..] : value;
        byte[] key;
        try
        {
            key = Convert.FromBase64String(encoded);
        }
        catch (FormatException ex)
        {
            throw new FormatException("Webhook secret is not valid base64.", ex);
        }

        if (key.Length is < 24 or > 64)
        {
            throw new FormatException("Webhook secret must decode to 24–64 bytes.");
        }

        return new WebhookSecret(key);
    }

    public static string Generate() => Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}

public enum WebhookVerdict
{
    Valid,
    MissingHeaders,
    MalformedTimestamp,
    TimestampTooOld,
    TimestampInFuture,
    InvalidSignature,
}

public sealed record WebhookVerification(WebhookVerdict Verdict, string? MessageId)
{
    public bool IsValid => Verdict == WebhookVerdict.Valid;
}

/// <summary>Raw header values; the verifier never sees a parsed body, only the bytes that were signed.</summary>
public readonly record struct WebhookHeaders(string? Id, string? Timestamp, string? Signature)
{
    public const string IdHeader = "webhook-id";
    public const string TimestampHeader = "webhook-timestamp";
    public const string SignatureHeader = "webhook-signature";
}

/// <summary>
/// Verifies Standard Webhooks signatures (<c>v1,base64(HMAC-SHA256(secret, id.timestamp.body))</c>).
/// <list type="bullet">
/// <item>Freshness: the signed timestamp must be within <see cref="Tolerance"/> of the receiver clock in both
/// directions, so a captured request cannot be replayed later. Inside the window, replays are stopped by the
/// persisted unique message id (see ADR 0001), not here.</item>
/// <item>Constant time: candidate signatures are compared with <see cref="CryptographicOperations.FixedTimeEquals"/>
/// on fixed-length MACs, so response time does not reveal how many leading bytes matched.</item>
/// <item>Rotation: several secrets can be active; the sender may put several space-separated signatures in one
/// header. Any (secret, signature) match is accepted.</item>
/// </list>
/// </summary>
public sealed class WebhookVerifier
{
    private const int MacLength = 32;
    private readonly WebhookSecret[] _secrets;
    private readonly TimeProvider _clock;

    public WebhookVerifier(IEnumerable<WebhookSecret> secrets, TimeProvider clock, TimeSpan? tolerance = null)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        _secrets = [.. secrets];
        if (_secrets.Length == 0)
        {
            throw new ArgumentException("At least one webhook secret is required.", nameof(secrets));
        }

        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Tolerance = tolerance ?? TimeSpan.FromMinutes(5);
    }

    public TimeSpan Tolerance { get; }

    public WebhookVerification Verify(WebhookHeaders headers, ReadOnlySpan<byte> body)
    {
        if (string.IsNullOrEmpty(headers.Id) || string.IsNullOrEmpty(headers.Timestamp) || string.IsNullOrEmpty(headers.Signature))
        {
            return new(WebhookVerdict.MissingHeaders, null);
        }

        if (!long.TryParse(headers.Timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return new(WebhookVerdict.MalformedTimestamp, headers.Id);
        }

        var now = _clock.GetUtcNow().ToUnixTimeSeconds();
        var toleranceSeconds = (long)Tolerance.TotalSeconds;
        if (seconds < now - toleranceSeconds)
        {
            return new(WebhookVerdict.TimestampTooOld, headers.Id);
        }

        if (seconds > now + toleranceSeconds)
        {
            return new(WebhookVerdict.TimestampInFuture, headers.Id);
        }

        Span<byte> expected = stackalloc byte[MacLength];
        Span<byte> candidate = stackalloc byte[MacLength];
        var matched = false;
        foreach (var secret in _secrets)
        {
            ComputeMac(secret, headers.Id, headers.Timestamp, body, expected);
            foreach (var part in headers.Signature.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!part.StartsWith("v1,", StringComparison.Ordinal)
                    || !Convert.TryFromBase64String(part[3..], candidate, out var written)
                    || written != MacLength)
                {
                    continue;
                }

                // No early exit: every candidate is compared, so timing does not depend on which secret matched.
                matched |= CryptographicOperations.FixedTimeEquals(expected, candidate);
            }
        }

        return new(matched ? WebhookVerdict.Valid : WebhookVerdict.InvalidSignature, headers.Id);
    }

    internal static void ComputeMac(WebhookSecret secret, string id, string timestamp, ReadOnlySpan<byte> body, Span<byte> destination)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, secret.Key);
        hmac.AppendData(Encoding.UTF8.GetBytes(id + "." + timestamp + "."));
        hmac.AppendData(body);
        hmac.GetHashAndReset(destination);
    }
}

/// <summary>Sender side, used by the visit simulator, the load test and the tests.</summary>
public sealed class WebhookSigner(IEnumerable<WebhookSecret> secrets)
{
    private readonly WebhookSecret[] _secrets = [.. secrets];

    public string Sign(string id, long timestamp, ReadOnlySpan<byte> body)
    {
        var ts = timestamp.ToString(CultureInfo.InvariantCulture);
        Span<byte> mac = stackalloc byte[32];
        var parts = new List<string>(_secrets.Length);
        foreach (var secret in _secrets)
        {
            WebhookVerifier.ComputeMac(secret, id, ts, body, mac);
            parts.Add("v1," + Convert.ToBase64String(mac));
        }

        return string.Join(' ', parts);
    }
}
