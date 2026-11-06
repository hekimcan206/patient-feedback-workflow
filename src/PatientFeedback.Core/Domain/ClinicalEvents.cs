using System.Text.Json.Serialization;
using PatientFeedback.Dispatch;

namespace PatientFeedback.Domain;

/// <summary>
/// Payload of a <c>visit.completed</c> event pushed by the hospital information system (HIS). Only the fields the
/// survey needs are accepted (data minimisation): no diagnosis, no national id, no clinical notes.
/// </summary>
public sealed record VisitCompletedEvent
{
    public const string EventType = "visit.completed";

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>The HIS visit/encounter number, opaque to this system.</summary>
    [JsonPropertyName("visitId")]
    public required string VisitId { get; init; }

    /// <summary>The HIS patient reference. Never stored in clear: only its keyed blind index.</summary>
    [JsonPropertyName("patientRef")]
    public required string PatientRef { get; init; }

    [JsonPropertyName("patientName")]
    public required string PatientName { get; init; }

    /// <summary>E.164 mobile number, e.g. +905321234567.</summary>
    [JsonPropertyName("phone")]
    public required string Phone { get; init; }

    [JsonPropertyName("department")]
    public required string Department { get; init; }

    [JsonPropertyName("completedAt")]
    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>Explicit consent to be contacted for service-quality surveys, captured at registration.</summary>
    [JsonPropertyName("surveyConsent")]
    public required bool SurveyConsent { get; init; }

    [JsonPropertyName("preferredChannel")]
    public Channel PreferredChannel { get; init; } = Channel.WhatsApp;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Type != EventType)
        {
            errors.Add($"type must be '{EventType}'.");
        }

        Require(VisitId, 64, "visitId", errors);
        Require(PatientRef, 64, "patientRef", errors);
        Require(PatientName, 200, "patientName", errors);
        Require(Department, 64, "department", errors);
        if (!IsE164(Phone))
        {
            errors.Add("phone must be an E.164 number (+ followed by 8–15 digits).");
        }

        return errors;
    }

    private static void Require(string? value, int max, string name, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max)
        {
            errors.Add($"{name} is required and limited to {max} characters.");
        }
    }

    internal static bool IsE164(string? phone) =>
        phone is { Length: >= 9 and <= 16 } && phone[0] == '+' && phone.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0 && phone[1] != '0';
}

public enum SurveyStatus
{
    Scheduled,
    Sent,
    Completed,
    Expired,
    Undeliverable,
    OptedOut,
}
