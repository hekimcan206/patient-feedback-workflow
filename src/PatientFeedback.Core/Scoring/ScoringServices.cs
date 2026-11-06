namespace PatientFeedback.Scoring;

/// <summary>One metric's aggregation rule, so reports can iterate metrics without knowing their formulas.</summary>
public interface IMetricScorer
{
    Metric Metric { get; }

    AggregateScore Aggregate(IEnumerable<int> scores);
}

/// <summary>Result of an aggregate score together with the sample it was computed from.</summary>
/// <remarks>A score without its sample size is not reportable, so the count travels with the value.</remarks>
public sealed record AggregateScore(Metric Metric, double? Value, int Responses)
{
    public bool HasData => Value is not null;
}

/// <summary>
/// NPS = % promoters (9–10) − % detractors (0–6), over all responses including passives (7–8).
/// Range −100 … +100. Computed on integer counts first so the result does not depend on response order.
/// </summary>
public sealed class NpsScoringService : IMetricScorer
{
    public Metric Metric => Metric.Nps;

    public static NpsCategory Classify(int score)
    {
        MetricScale.Validate(Metric.Nps, score);
        return score switch
        {
            >= 9 => NpsCategory.Promoter,
            >= 7 => NpsCategory.Passive,
            _ => NpsCategory.Detractor,
        };
    }

    public AggregateScore Aggregate(IEnumerable<int> scores)
    {
        ArgumentNullException.ThrowIfNull(scores);
        int promoters = 0, detractors = 0, total = 0;
        foreach (var score in scores)
        {
            switch (Classify(score))
            {
                case NpsCategory.Promoter: promoters++; break;
                case NpsCategory.Detractor: detractors++; break;
            }

            total++;
        }

        return total == 0
            ? new AggregateScore(Metric.Nps, null, 0)
            : new AggregateScore(Metric.Nps, Round(100.0 * (promoters - detractors) / total), total);
    }

    internal static double Round(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);
}

/// <summary>
/// CSAT = share of "satisfied" answers (top-two-box: 4 or 5 on the 1–5 scale) × 100. Neutral and negative answers stay
/// in the denominator; averaging the raw 1–5 values instead would hide polarisation (half 5s and half 1s = "3.0").
/// </summary>
public sealed class CsatScoringService : IMetricScorer
{
    public Metric Metric => Metric.Csat;

    public const int SatisfiedThreshold = 4;

    public static bool IsSatisfied(int score)
    {
        MetricScale.Validate(Metric.Csat, score);
        return score >= SatisfiedThreshold;
    }

    public AggregateScore Aggregate(IEnumerable<int> scores)
    {
        ArgumentNullException.ThrowIfNull(scores);
        int satisfied = 0, total = 0;
        foreach (var score in scores)
        {
            if (IsSatisfied(score))
            {
                satisfied++;
            }

            total++;
        }

        return total == 0
            ? new AggregateScore(Metric.Csat, null, 0)
            : new AggregateScore(Metric.Csat, NpsScoringService.Round(100.0 * satisfied / total), total);
    }
}

/// <summary>
/// CES on the 7-point agreement scale ("made it easy", 7 = strongly agree, so higher is better). Two published
/// conventions exist and are not convertible into each other; this service reports the <b>mean</b> as the headline
/// (comparable with CES 2.0 benchmarks) and the <b>top-box share</b> (5–7) as a secondary figure, each labelled.
/// </summary>
public sealed class CesScoringService : IMetricScorer
{
    public const int EasyThreshold = 5;

    public Metric Metric => Metric.Ces;

    public AggregateScore Aggregate(IEnumerable<int> scores) => Compute(scores).Mean;

    public static (AggregateScore Mean, AggregateScore TopBoxPercent) Compute(IEnumerable<int> scores)
    {
        ArgumentNullException.ThrowIfNull(scores);
        long sum = 0;
        int easy = 0, total = 0;
        foreach (var score in scores)
        {
            MetricScale.Validate(Metric.Ces, score);
            sum += score;
            if (score >= EasyThreshold)
            {
                easy++;
            }

            total++;
        }

        if (total == 0)
        {
            return (new AggregateScore(Metric.Ces, null, 0), new AggregateScore(Metric.Ces, null, 0));
        }

        return (
            new AggregateScore(Metric.Ces, Math.Round((double)sum / total, 2, MidpointRounding.AwayFromZero), total),
            new AggregateScore(Metric.Ces, NpsScoringService.Round(100.0 * easy / total), total));
    }
}

/// <summary>Facade used by the web layer: one call turns a set of answers into the three headline figures.</summary>
public sealed class ScoringService(NpsScoringService nps, CsatScoringService csat)
{
    public ScoringService()
        : this(new NpsScoringService(), new CsatScoringService())
    {
    }

    public ScoreSummary Summarise(IReadOnlyCollection<SurveyAnswers> answers)
    {
        ArgumentNullException.ThrowIfNull(answers);
        var (cesMean, cesTopBox) = CesScoringService.Compute(answers.Where(a => a.Ces is not null).Select(a => a.Ces!.Value));
        return new ScoreSummary(
            nps.Aggregate(answers.Select(a => a.Nps)),
            csat.Aggregate(answers.Where(a => a.Csat is not null).Select(a => a.Csat!.Value)),
            cesMean,
            cesTopBox);
    }
}

public sealed record ScoreSummary(AggregateScore Nps, AggregateScore Csat, AggregateScore CesMean, AggregateScore CesTopBox);
