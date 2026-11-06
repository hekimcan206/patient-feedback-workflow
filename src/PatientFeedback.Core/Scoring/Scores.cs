namespace PatientFeedback.Scoring;

/// <summary>The three standard survey metrics and the scale each one is collected on.</summary>
public enum Metric
{
    /// <summary>Net Promoter Score: "How likely are you to recommend us?" 0–10.</summary>
    Nps,

    /// <summary>Customer (patient) Satisfaction: "How satisfied were you with your visit?" 1–5.</summary>
    Csat,

    /// <summary>Customer Effort Score: "The hospital made it easy to get what I needed." 1–7 agreement.</summary>
    Ces,
}

public static class MetricScale
{
    public static (int Min, int Max) Of(Metric metric) => metric switch
    {
        Metric.Nps => (0, 10),
        Metric.Csat => (1, 5),
        Metric.Ces => (1, 7),
        _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, null),
    };

    public static bool IsValid(Metric metric, int value)
    {
        var (min, max) = Of(metric);
        return value >= min && value <= max;
    }

    public static void Validate(Metric metric, int value)
    {
        if (!IsValid(metric, value))
        {
            var (min, max) = Of(metric);
            throw new ArgumentOutOfRangeException(nameof(value), value, $"{metric} must be between {min} and {max}.");
        }
    }
}

/// <summary>NPS answer bands. Passives (7–8) count in the denominator but in neither percentage.</summary>
public enum NpsCategory
{
    Detractor,
    Passive,
    Promoter,
}

/// <summary>
/// One completed survey. NPS is mandatory (it is the headline metric); CSAT and CES are optional so a patient who
/// abandons after the first question still produces a usable answer.
/// </summary>
public sealed record SurveyAnswers(int Nps, int? Csat, int? Ces, string? Comment)
{
    public void Validate()
    {
        MetricScale.Validate(Metric.Nps, Nps);
        if (Csat is { } csat)
        {
            MetricScale.Validate(Metric.Csat, csat);
        }

        if (Ces is { } ces)
        {
            MetricScale.Validate(Metric.Ces, ces);
        }

        if (Comment is { Length: > SurveyLimits.MaxCommentLength })
        {
            throw new ArgumentOutOfRangeException(nameof(Comment), $"Comment is limited to {SurveyLimits.MaxCommentLength} characters.");
        }
    }
}

public static class SurveyLimits
{
    public const int MaxCommentLength = 400;
}
