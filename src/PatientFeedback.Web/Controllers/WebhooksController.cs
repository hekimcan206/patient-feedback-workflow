using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PatientFeedback.Dispatch;
using PatientFeedback.Domain;
using PatientFeedback.Oracle;
using PatientFeedback.Security;
using PatientFeedback.Web.Dispatch;
using PatientFeedback.Web.Infrastructure;

namespace PatientFeedback.Web.Controllers;

/// <summary>Keyed verifiers, so the HIS and the messaging providers cannot sign for each other.</summary>
public sealed class WebhookVerifiers(WebhookVerifier clinicalEvents, WebhookVerifier providers)
{
    public WebhookVerifier ClinicalEvents { get; } = clinicalEvents;

    public WebhookVerifier Providers { get; } = providers;
}

public sealed record WebhookAccepted(string Outcome, long? SurveyId, DateTimeOffset? ScheduledFor);

public sealed record InboundReply(string From, string Text);

[ApiController]
[AllowAnonymous]
[IgnoreAntiforgeryToken] // authenticated by HMAC signature, not by cookie
[Route("webhooks")]
public sealed partial class WebhooksController(
    WebhookVerifiers verifiers,
    IntakeRepository intake,
    ContactRepository contacts,
    DispatchSignal dispatch,
    FeedbackMetrics metrics,
    IOptions<WebhookOptions> options,
    TimeProvider clock,
    ILogger<WebhooksController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// Receives <c>visit.completed</c> from the HIS. Order matters: size limit → signature over the raw bytes →
    /// only then parse JSON. An unauthenticated body is never deserialised.
    /// </summary>
    [HttpPost("clinical-events")]
    public async Task<IActionResult> ClinicalEvent(CancellationToken ct)
    {
        var (body, error) = await ReadBodyAsync(ct);
        if (error is not null)
        {
            return error;
        }

        var verification = verifiers.ClinicalEvents.Verify(Headers(), body);
        if (!verification.IsValid)
        {
            metrics.Webhook(verification.Verdict.ToString());
            LogRejected(verification.Verdict, verification.MessageId);
            // One generic answer: the caller learns nothing about which check failed.
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid webhook signature");
        }

        VisitCompletedEvent? visit;
        try
        {
            visit = JsonSerializer.Deserialize<VisitCompletedEvent>(body, Json);
        }
        catch (JsonException)
        {
            visit = null;
        }

        var problems = visit?.Validate() ?? ["Body is not a visit.completed event."];
        if (visit is null || problems.Count > 0)
        {
            metrics.Webhook("Invalid");
            return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]> { ["body"] = [.. problems] }));
        }

        var result = await intake.RecordVisitAsync(verification.MessageId!, visit, ct);
        metrics.Webhook(result.Outcome.ToString());
        if (result.Outcome == IntakeOutcome.SurveyScheduled && result.ScheduledFor <= clock.GetUtcNow())
        {
            dispatch.Nudge();
        }

        // 202 for every accepted delivery, including duplicates: the sender must stop retrying either way.
        return Accepted(new WebhookAccepted(result.Outcome.ToString(), result.SurveyId, result.ScheduledFor));
    }

    /// <summary>Inbound replies from a messaging provider. "IPTAL", "DUR" or "STOP" opts the sender out of that channel.</summary>
    [HttpPost("channels/{channel}/inbound")]
    public async Task<IActionResult> Inbound(string channel, CancellationToken ct)
    {
        if (!Enum.TryParse<Channel>(channel, ignoreCase: true, out var parsed))
        {
            return NotFound();
        }

        var (body, error) = await ReadBodyAsync(ct);
        if (error is not null)
        {
            return error;
        }

        if (!verifiers.Providers.Verify(Headers(), body).IsValid)
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid webhook signature");
        }

        InboundReply? reply;
        try
        {
            reply = JsonSerializer.Deserialize<InboundReply>(body, Json);
        }
        catch (JsonException)
        {
            reply = null;
        }

        if (reply is null || string.IsNullOrWhiteSpace(reply.From) || reply.Text is null)
        {
            return ValidationProblem();
        }

        if (!IsStopKeyword(reply.Text))
        {
            return Ok(new { optedOut = 0 });
        }

        var count = await contacts.OptOutByPhoneAsync(reply.From, parsed, "provider:" + parsed.ToString().ToLowerInvariant(), clock.GetUtcNow(), ct);
        return Ok(new { optedOut = count });
    }

    internal static bool IsStopKeyword(string text)
    {
        var word = text.Trim().ToUpperInvariant().Replace('İ', 'I');
        return word is "IPTAL" or "DUR" or "STOP" or "HAYIR";
    }

    private WebhookHeaders Headers() => new(
        Request.Headers[WebhookHeaders.IdHeader].ToString(),
        Request.Headers[WebhookHeaders.TimestampHeader].ToString(),
        Request.Headers[WebhookHeaders.SignatureHeader].ToString());

    private async Task<(byte[] Body, IActionResult? Error)> ReadBodyAsync(CancellationToken ct)
    {
        var max = options.Value.MaxBodyBytes;
        if (Request.ContentLength > max)
        {
            return ([], Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Payload too large"));
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await Request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > max)
            {
                return ([], Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Payload too large"));
            }

            buffer.Write(chunk, 0, read);
        }

        return (buffer.ToArray(), null);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected webhook {MessageId}: {Verdict}")]
    private partial void LogRejected(WebhookVerdict verdict, string? messageId);
}
