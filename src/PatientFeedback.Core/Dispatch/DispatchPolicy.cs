namespace PatientFeedback.Dispatch;

public sealed class DispatchOptions
{
    /// <summary>Delay between the visit and the survey. Asking while the patient is still in the car park biases answers.</summary>
    public TimeSpan SendDelay { get; set; } = TimeSpan.FromHours(2);

    /// <summary>No messages inside quiet hours (local time); a survey that falls inside is moved to their end.</summary>
    public bool QuietHoursEnabled { get; set; } = true;

    public TimeOnly QuietHoursStart { get; set; } = new(21, 0);

    public TimeOnly QuietHoursEnd { get; set; } = new(9, 0);

    public string TimeZone { get; set; } = "Europe/Istanbul";

    /// <summary>How long a survey link stays valid.</summary>
    public TimeSpan LinkLifetime { get; set; } = TimeSpan.FromDays(7);

    public int MaxAttemptsPerChannel { get; set; } = 5;

    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>When a survey may be sent.</summary>
public sealed class DispatchSchedule(DispatchOptions options)
{
    private readonly TimeZoneInfo _zone = ResolveZone(options.TimeZone);

    // Chiseled runtime images ship without tzdata. Türkiye has observed UTC+3 all year since 2016, so the fixed-offset
    // fallback is exact for the default zone; other zones need the tzdata package in the image.
    internal static TimeZoneInfo ResolveZone(string id) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone)
            ? zone
            : id == "Europe/Istanbul"
                ? TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromHours(3), id, id)
                : throw new TimeZoneNotFoundException($"Time zone {id} is not available in this runtime.");

    public DateTimeOffset SendAt(DateTimeOffset visitCompletedAt, DateTimeOffset now)
    {
        var candidate = visitCompletedAt + options.SendDelay;
        if (candidate < now)
        {
            candidate = now;
        }

        return options.QuietHoursEnabled ? OutsideQuietHours(candidate) : candidate;
    }

    internal DateTimeOffset OutsideQuietHours(DateTimeOffset instant)
    {
        var local = TimeZoneInfo.ConvertTime(instant, _zone);
        var time = TimeOnly.FromDateTime(local.DateTime);
        var start = options.QuietHoursStart;
        var end = options.QuietHoursEnd;
        var overnight = start > end;
        var inQuiet = overnight ? time >= start || time < end : time >= start && time < end;
        if (!inQuiet)
        {
            return instant;
        }

        var day = DateOnly.FromDateTime(local.DateTime);
        if (overnight && time >= start)
        {
            day = day.AddDays(1);
        }

        var endLocal = day.ToDateTime(end);
        return new DateTimeOffset(endLocal, _zone.GetUtcOffset(endLocal)).ToUniversalTime();
    }
}

/// <summary>Exponential back-off with full jitter (AWS architecture blog), never shorter than a provider Retry-After.</summary>
public sealed class RetryPolicy(DispatchOptions options, Func<double>? random = null)
{
    private readonly Func<double> _random = random ?? Random.Shared.NextDouble;

    public TimeSpan Delay(int attempt, TimeSpan? retryAfter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        var ceiling = Math.Min(options.RetryMaxDelay.TotalMilliseconds, options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
        var jittered = TimeSpan.FromMilliseconds(_random() * ceiling);
        return retryAfter is { } ra && ra > jittered ? ra : jittered;
    }
}

public enum OutcomeKind
{
    Sent,
    RetryLater,
    FallBack,
    OptedOut,
    Dead,
}

/// <summary>What to do with an outbox message after a send attempt.</summary>
public sealed record DispatchOutcome(OutcomeKind Kind, string? Detail = null, DateTimeOffset? NextAttemptAt = null, Channel? NextChannel = null);

/// <summary>
/// Pure decision logic of the dispatcher: given the attempt result, the channels still usable for this contact and
/// the attempt count, decide the next state. Kept free of I/O so every branch is unit-tested.
/// </summary>
public sealed class DispatchPlanner(DispatchOptions options, RetryPolicy retry)
{
    /// <summary>Channel order for a contact: preferred first, then the other, minus channels the contact opted out of.</summary>
    public static IReadOnlyList<Channel> Route(Channel preferred, IReadOnlySet<Channel> optedOut)
    {
        ArgumentNullException.ThrowIfNull(optedOut);
        var order = preferred == Channel.WhatsApp ? new[] { Channel.WhatsApp, Channel.Sms } : [Channel.Sms, Channel.WhatsApp];
        return [.. order.Where(c => !optedOut.Contains(c))];
    }

    public DispatchOutcome Decide(SendResult result, Channel current, int attemptsOnChannel, IReadOnlyList<Channel> route, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(route);
        switch (result.Status)
        {
            case SendStatus.Accepted:
                return new(OutcomeKind.Sent, result.ProviderMessageId);

            case SendStatus.Transient when attemptsOnChannel < options.MaxAttemptsPerChannel:
                return new(OutcomeKind.RetryLater, result.ErrorCode, now + retry.Delay(attemptsOnChannel, result.RetryAfter));

            case SendStatus.OptedOut:
                // The opt-out is recorded by the caller; the next channel (if the contact has not opted out there) is tried.
                return Next(route, current) is { } afterOptOut
                    ? new(OutcomeKind.FallBack, result.ErrorCode, now, afterOptOut)
                    : new(OutcomeKind.OptedOut, result.ErrorCode);

            default:
                // Permanent error, or transient errors exhausted their attempts on this channel.
                return Next(route, current) is { } next
                    ? new(OutcomeKind.FallBack, result.ErrorCode, now, next)
                    : new(OutcomeKind.Dead, result.ErrorCode);
        }
    }

    private static Channel? Next(IReadOnlyList<Channel> route, Channel current)
    {
        var index = -1;
        for (var i = 0; i < route.Count; i++)
        {
            if (route[i] == current)
            {
                index = i;
                break;
            }
        }

        return index >= 0 && index + 1 < route.Count ? route[index + 1] : null;
    }
}
