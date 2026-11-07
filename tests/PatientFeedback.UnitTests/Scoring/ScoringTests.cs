using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using PatientFeedback.Scoring;

namespace PatientFeedback.UnitTests.Scoring;

public sealed class ScoringTests
{
    private readonly NpsScoringService _nps = new();
    private readonly CsatScoringService _csat = new();

    [Theory]
    [InlineData(0, NpsCategory.Detractor)]
    [InlineData(6, NpsCategory.Detractor)]
    [InlineData(7, NpsCategory.Passive)]
    [InlineData(8, NpsCategory.Passive)]
    [InlineData(9, NpsCategory.Promoter)]
    [InlineData(10, NpsCategory.Promoter)]
    public void Nps_bands_follow_the_standard_cut_points(int score, NpsCategory expected) =>
        NpsScoringService.Classify(score).Should().Be(expected);

    [Fact]
    public void Nps_is_percent_promoters_minus_percent_detractors_with_passives_in_the_denominator()
    {
        // 5 promoters, 3 passives, 2 detractors: 50% − 20% = +30.
        var result = _nps.Aggregate([10, 9, 9, 10, 9, 8, 7, 7, 3, 6]);

        result.Value.Should().Be(30);
        result.Responses.Should().Be(10);
    }

    [Fact]
    public void Nps_hits_both_extremes()
    {
        _nps.Aggregate([10, 9, 10]).Value.Should().Be(100);
        _nps.Aggregate([0, 6, 3]).Value.Should().Be(-100);
        _nps.Aggregate([7, 8]).Value.Should().Be(0);
    }

    [Fact]
    public void Nps_rounds_to_one_decimal_on_counts_not_on_percentages()
    {
        // 1 promoter of 3 = 33.333… → 33.3 (rounding the two percentages separately could give 33.4 or 33.3).
        _nps.Aggregate([9, 7, 8]).Value.Should().Be(33.3);
    }

    [Fact]
    public void Empty_sample_has_no_score_rather_than_zero() =>
        _nps.Aggregate([]).Should().Be(new AggregateScore(Metric.Nps, null, 0));

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    public void Out_of_scale_answers_are_rejected(int score) =>
        FluentActions.Invoking(() => _nps.Aggregate([score])).Should().Throw<ArgumentOutOfRangeException>();

    [Fact]
    public void Csat_is_top_two_box_share()
    {
        // 4 and 5 are satisfied: 3 of 5 = 60%. A mean would report 3.2 and hide the split.
        _csat.Aggregate([5, 4, 4, 1, 2]).Value.Should().Be(60);
    }

    [Fact]
    public void Ces_reports_mean_and_top_box_separately()
    {
        var (mean, topBox) = CesScoringService.Compute([7, 6, 5, 2, 1]);

        mean.Value.Should().Be(4.2);
        topBox.Value.Should().Be(60);
    }

    [Fact]
    public void Summary_skips_unanswered_optional_questions_instead_of_counting_them_as_low()
    {
        var summary = new ScoringService().Summarise(
        [
            new SurveyAnswers(10, 5, null, null),
            new SurveyAnswers(0, null, null, null),
        ]);

        summary.Nps.Responses.Should().Be(2);
        summary.Csat.Responses.Should().Be(1);
        summary.Csat.Value.Should().Be(100);
        summary.CesMean.HasData.Should().BeFalse();
    }

    // ---- properties ------------------------------------------------------------------------------------

    private static Arbitrary<int[]> NpsSamples() =>
        Gen.Choose(0, 10).ArrayOf().Where(a => a.Length > 0).ToArbitrary();

    [Property]
    public Property Nps_is_always_between_minus_100_and_100() =>
        Prop.ForAll(NpsSamples(), scores =>
        {
            var value = _nps.Aggregate(scores).Value!.Value;
            return value is >= -100 and <= 100;
        });

    [Property]
    public Property Nps_does_not_depend_on_response_order() =>
        Prop.ForAll(NpsSamples(), scores =>
            _nps.Aggregate(scores).Value == _nps.Aggregate(scores.Reverse().OrderBy(s => s % 3)).Value);

    [Property]
    public Property Adding_a_promoter_never_lowers_and_adding_a_detractor_never_raises_nps() =>
        Prop.ForAll(NpsSamples(), Gen.Choose(9, 10).ToArbitrary(), Gen.Choose(0, 6).ToArbitrary(), (scores, promoter, detractor) =>
        {
            var before = _nps.Aggregate(scores).Value!.Value;
            return _nps.Aggregate([.. scores, promoter]).Value >= before
                && _nps.Aggregate([.. scores, detractor]).Value <= before;
        });

    [Property]
    public Property Nps_equals_the_formula_on_counts() =>
        Prop.ForAll(NpsSamples(), scores =>
        {
            var expected = Math.Round(100.0 * (scores.Count(s => s >= 9) - scores.Count(s => s <= 6)) / scores.Length, 1, MidpointRounding.AwayFromZero);
            return _nps.Aggregate(scores).Value == expected;
        });

    [Property]
    public Property Csat_is_a_percentage_and_ces_mean_stays_on_its_scale() =>
        Prop.ForAll(
            Gen.Choose(1, 5).ArrayOf().Where(a => a.Length > 0).ToArbitrary(),
            Gen.Choose(1, 7).ArrayOf().Where(a => a.Length > 0).ToArbitrary(),
            (csat, ces) =>
            {
                var c = _csat.Aggregate(csat).Value!.Value;
                var (mean, topBox) = CesScoringService.Compute(ces);
                return c is >= 0 and <= 100
                    && mean.Value >= ces.Min() && mean.Value <= ces.Max()
                    && topBox.Value is >= 0 and <= 100;
            });
}
