using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;
using PatientFeedback.Dispatch;
using PatientFeedback.Oracle;
using PatientFeedback.Privacy;
using PatientFeedback.Web.Infrastructure;

namespace PatientFeedback.Web.Dispatch;

/// <summary>
/// Wakes the dispatcher right after a survey is committed, so an immediate survey does not wait for the next poll.
/// The poll remains the guarantee (other instances, retries, scheduled sends); the nudge only removes idle latency.
/// </summary>
public sealed class DispatchSignal
{
    private readonly System.Threading.Channels.Channel<bool> _channel = System.Threading.Channels.Channel.CreateBounded<bool>(
        new System.Threading.Channels.BoundedChannelOptions(1) { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite });

    public void Nudge() => _channel.Writer.TryWrite(true);

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await _channel.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
    }
}

/// <summary>
/// Drains the outbox: claims due messages (SKIP LOCKED, leased), decrypts phone and link token just for the send,
/// calls the channel adapter, and records the planner's decision. Safe to run on several instances.
/// </summary>
public sealed partial class SurveyDispatcher(
    OutboxRepository outbox,
    PiiProtector protector,
    IEnumerable<IChannelAdapter> adapters,
    DispatchPlanner planner,
    DispatchSignal signal,
    IOptions<DispatcherOptions> options,
    IOptions<SurveyOptions> survey,
    FeedbackMetrics metrics,
    TimeProvider clock,
    ILogger<SurveyDispatcher> logger) : BackgroundService
{
    private const string Actor = "dispatcher";
    private readonly Dictionary<Channel, IChannelAdapter> _adapters = adapters.ToDictionary(a => a.Channel);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            int processed;
            try
            {
                processed = await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // The loop must survive a database blip; leased rows are re-claimed after the lease.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogLoopFailed(ex);
                processed = 0;
                await Task.Delay(options.Value.PollInterval, clock, stoppingToken);
            }

            // A full batch means more work is probably waiting: claim again immediately.
            if (processed < options.Value.BatchSize)
            {
                await signal.WaitAsync(options.Value.PollInterval, stoppingToken);
            }
        }
    }

    /// <summary>One claim-and-send cycle. Returns the number of messages handled.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var o = options.Value;
        var (claimToken, messages) = await outbox.ClaimAsync(o.BatchSize, o.Lease, clock.GetUtcNow(), ct);
        if (messages.Count == 0)
        {
            return 0;
        }

        // The access is audited before any phone number is decrypted.
        await outbox.AuditDispatchReadsAsync(messages.Select(m => m.ContactId).Distinct(StringComparer.Ordinal).ToList(), Actor, ct);
        await Parallel.ForEachAsync(messages, new ParallelOptions { MaxDegreeOfParallelism = o.Concurrency, CancellationToken = ct },
            async (message, token) => await HandleAsync(message, claimToken, token));
        return messages.Count;
    }

    private async Task HandleAsync(ClaimedMessage m, Guid claimToken, CancellationToken ct)
    {
        using var activity = FeedbackMetrics.Activities.StartActivity("dispatch survey");
        activity?.SetTag("pf.message_id", m.MessageId);
        activity?.SetTag("pf.channel", m.ChannelCode);
        var now = clock.GetUtcNow();

        if (m.WrappedDek is null || m.KekId is null || m.PhoneCt is null || m.TokenCt is null)
        {
            await outbox.FinishAsync(m, claimToken, dead: false, "EXPIRED", "contact anonymised or token destroyed", ct);
            return;
        }

        if (m.ExpiresAt <= now)
        {
            await outbox.FinishAsync(m, claimToken, dead: false, "EXPIRED", "link expired before delivery", ct);
            return;
        }

        var route = DispatchPlanner.Route(m.PreferredChannel, m.OptedOut);
        if (!route.Contains(m.Channel))
        {
            // The contact opted out of this channel after the message was queued.
            if (route.Count == 0)
            {
                await outbox.FinishAsync(m, claimToken, dead: false, "OPTED_OUT", "opted out of all channels", ct);
            }
            else
            {
                await outbox.FallBackAsync(m, claimToken, route[0], "opted out of " + m.ChannelCode, now, ct);
            }

            return;
        }

        OutboundMessage outbound;
        using (var key = protector.Unwrap(new WrappedKey(m.KekId, m.WrappedDek), m.ContactId))
        {
            var token = PiiProtector.Decrypt(key, m.ContactId, IntakeRepository.SurveyTokenField(m.SurveyId), m.TokenCt);
            outbound = new OutboundMessage(
                m.MessageId,
                m.Channel,
                PiiProtector.Decrypt(key, m.ContactId, ContactFields.Phone, m.PhoneCt),
                "visit_feedback_v1",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["department"] = m.Department,
                    ["link"] = survey.Value.PublicBaseUrl.TrimEnd('/') + "/s/" + token,
                });
        }

        var result = await SendWithTimeoutAsync(outbound, ct);
        var outcome = planner.Decide(result, m.Channel, m.Attempts + 1, route, clock.GetUtcNow());
        activity?.SetTag("pf.outcome", outcome.Kind.ToString());

        switch (outcome.Kind)
        {
            case OutcomeKind.Sent:
                await outbox.MarkSentAsync(m, claimToken, outcome.Detail ?? "", clock.GetUtcNow(), ct);
                break;
            case OutcomeKind.RetryLater:
                await outbox.RetryAsync(m, claimToken, outcome.NextAttemptAt!.Value, outcome.Detail, ct);
                break;
            case OutcomeKind.FallBack:
                if (result.Status == SendStatus.OptedOut)
                {
                    await outbox.RecordOptOutAsync(m.ContactId, m.Channel, "provider:" + m.ChannelCode.ToLowerInvariant(), clock.GetUtcNow(), ct);
                }

                await outbox.FallBackAsync(m, claimToken, outcome.NextChannel!.Value, outcome.Detail, clock.GetUtcNow(), ct);
                break;
            case OutcomeKind.OptedOut:
                await outbox.RecordOptOutAsync(m.ContactId, m.Channel, "provider:" + m.ChannelCode.ToLowerInvariant(), clock.GetUtcNow(), ct);
                await outbox.FinishAsync(m, claimToken, dead: false, "OPTED_OUT", outcome.Detail, ct);
                break;
            case OutcomeKind.Dead:
                LogUndeliverable(m.MessageId, m.TotalAttempts + 1, outcome.Detail);
                await outbox.FinishAsync(m, claimToken, dead: true, "UNDELIVERABLE", outcome.Detail, ct);
                break;
        }
    }

    private async Task<SendResult> SendWithTimeoutAsync(OutboundMessage message, CancellationToken ct)
    {
        var adapter = _adapters[message.Channel];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Value.ProviderTimeout);
        var started = Stopwatch.GetTimestamp();
        SendResult result;
        try
        {
            result = await adapter.SendAsync(message, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result = SendResult.Transient("client-timeout");
        }
        catch (HttpRequestException ex)
        {
            result = SendResult.Transient("http-" + (ex.StatusCode is { } code ? ((int)code).ToString(CultureInfo.InvariantCulture) : "network"));
        }

        metrics.SendAttempt(message.Channel.ToString(), result.Status.ToString(), Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return result;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Dispatcher cycle failed; retrying after the poll interval")]
    private partial void LogLoopFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} undeliverable after {Attempts} attempts: {Reason}")]
    private partial void LogUndeliverable(long messageId, int attempts, string? reason);
}
