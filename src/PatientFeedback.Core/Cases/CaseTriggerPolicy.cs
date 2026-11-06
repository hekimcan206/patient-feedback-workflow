using PatientFeedback.Scoring;

namespace PatientFeedback.Cases;

public enum CaseSeverity
{
    Low = 1,
    Medium = 2,
    High = 3,
}

public enum CaseStatus
{
    Open,
    InProgress,
    Resolved,
}

/// <summary>Why a case was opened: each low answer that crossed a threshold is listed, worst first.</summary>
public sealed record CaseTrigger(CaseSeverity Severity, TimeSpan Sla, IReadOnlyList<string> Reasons);

/// <summary>Thresholds and SLAs. Defaults are illustrative and configurable per hospital.</summary>
public sealed class CaseTriggerOptions
{
    /// <summary>NPS at or below this opens a High case (strong detractor: likely complaint).</summary>
    public int NpsHighAtOrBelow { get; set; } = 3;

    /// <summary>NPS at or below this (and above <see cref="NpsHighAtOrBelow"/>) opens a Medium case. 6 = every detractor.</summary>
    public int NpsMediumAtOrBelow { get; set; } = 6;

    /// <summary>CSAT at or below this opens a Medium case (dissatisfied / very dissatisfied).</summary>
    public int CsatMediumAtOrBelow { get; set; } = 2;

    /// <summary>CES at or below this opens a Low case (disagrees the visit was easy).</summary>
    public int CesLowAtOrBelow { get; set; } = 2;

    public TimeSpan HighSla { get; set; } = TimeSpan.FromHours(4);

    public TimeSpan MediumSla { get; set; } = TimeSpan.FromHours(24);

    public TimeSpan LowSla { get; set; } = TimeSpan.FromHours(72);
}

/// <summary>
/// Decides whether a completed survey opens a service-recovery case. One case per survey: the worst answer sets the
/// severity (and therefore the SLA); every crossing answer is listed as a reason so the case handler sees the full
/// picture without opening the survey.
/// </summary>
public sealed class CaseTriggerPolicy(CaseTriggerOptions options)
{
    public CaseTriggerPolicy()
        : this(new CaseTriggerOptions())
    {
    }

    public CaseTrigger? Evaluate(SurveyAnswers answers)
    {
        ArgumentNullException.ThrowIfNull(answers);
        answers.Validate();

        var hits = new List<(CaseSeverity Severity, string Reason)>();
        if (answers.Nps <= options.NpsHighAtOrBelow)
        {
            hits.Add((CaseSeverity.High, $"NPS {answers.Nps}/10 (strong detractor)"));
        }
        else if (answers.Nps <= options.NpsMediumAtOrBelow)
        {
            hits.Add((CaseSeverity.Medium, $"NPS {answers.Nps}/10 (detractor)"));
        }

        if (answers.Csat is { } csat && csat <= options.CsatMediumAtOrBelow)
        {
            hits.Add((CaseSeverity.Medium, $"CSAT {csat}/5 (dissatisfied)"));
        }

        if (answers.Ces is { } ces && ces <= options.CesLowAtOrBelow)
        {
            hits.Add((CaseSeverity.Low, $"CES {ces}/7 (high effort)"));
        }

        if (hits.Count == 0)
        {
            return null;
        }

        var severity = hits.Max(h => h.Severity);
        return new CaseTrigger(
            severity,
            SlaFor(severity),
            [.. hits.OrderByDescending(h => h.Severity).Select(h => h.Reason)]);
    }

    public TimeSpan SlaFor(CaseSeverity severity) => severity switch
    {
        CaseSeverity.High => options.HighSla,
        CaseSeverity.Medium => options.MediumSla,
        CaseSeverity.Low => options.LowSla,
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, null),
    };
}
