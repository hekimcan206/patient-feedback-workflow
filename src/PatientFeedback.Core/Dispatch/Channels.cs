namespace PatientFeedback.Dispatch;

public enum Channel
{
    WhatsApp,
    Sms,
}

/// <summary>What the dispatcher hands to a provider adapter. The phone number is decrypted only for this call.</summary>
public sealed record OutboundMessage(long MessageId, Channel Channel, string Phone, string TemplateName, IReadOnlyDictionary<string, string> Parameters)
{
    /// <summary>Idempotency key sent to the provider so a retried HTTP call after a timeout does not double-send.</summary>
    public string IdempotencyKey => $"survey-msg-{MessageId}";
}

public enum SendStatus
{
    /// <summary>Provider accepted the message (delivery receipts arrive later).</summary>
    Accepted,

    /// <summary>Temporary: throttled, timeout, 5xx. Retry on the same channel with back-off.</summary>
    Transient,

    /// <summary>Permanent for this channel: number not on WhatsApp, invalid number. Try the next channel.</summary>
    Permanent,

    /// <summary>The recipient has opted out on this channel. Never retry; record the opt-out.</summary>
    OptedOut,
}

public sealed record SendResult(SendStatus Status, string? ProviderMessageId = null, string? ErrorCode = null, TimeSpan? RetryAfter = null)
{
    public static SendResult Accepted(string providerMessageId) => new(SendStatus.Accepted, providerMessageId);

    public static SendResult Transient(string code, TimeSpan? retryAfter = null) => new(SendStatus.Transient, ErrorCode: code, RetryAfter: retryAfter);

    public static SendResult Permanent(string code) => new(SendStatus.Permanent, ErrorCode: code);

    public static SendResult OptedOut(string code) => new(SendStatus.OptedOut, ErrorCode: code);
}

/// <summary>A messaging provider. Implementations must not throw for provider-side errors: they classify them.</summary>
public interface IChannelAdapter
{
    Channel Channel { get; }

    Task<SendResult> SendAsync(OutboundMessage message, CancellationToken cancellationToken);
}
