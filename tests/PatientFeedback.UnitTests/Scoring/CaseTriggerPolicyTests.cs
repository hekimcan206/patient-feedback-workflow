using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using PatientFeedback.Cases;
using PatientFeedback.Scoring;

namespace PatientFeedback.UnitTests.Scoring;

public sealed class CaseTriggerPolicyTests
{
    private readonly CaseTriggerPolicy _policy = new();

    [Theory]
    [InlineData(10, 5, 7, null)]
    [InlineData(7, 3, 3, null)]
    [InlineData(6, 5, 7, CaseSeverity.Medium)]
    [InlineData(3, 5, 7, CaseSeverity.High)]
    [InlineData(0, null, null, CaseSeverity.High)]
    [InlineData(9, 2, 7, CaseSeverity.Medium)]
    [InlineData(9, 5, 2, CaseSeverity.Low)]
    [InlineData(5, 1, 1, CaseSeverity.Medium)]
    public void Worst_answer_sets_the_severity(int nps, int? csat, int? ces, CaseSeverity? expected) =>
        _policy.Evaluate(new SurveyAnswers(nps, csat, ces, null))?.Severity.Should().Be(expected);

    [Fact]
    public void Every_crossing_answer_is_listed_worst_first_and_the_sla_follows_severity()
    {
        var trigger = _policy.Evaluate(new SurveyAnswers(2, 1, 1, null))!;

        trigger.Reasons.Should().Equal("NPS 2/10 (strong detractor)", "CSAT 1/5 (dissatisfied)", "CES 1/7 (high effort)");
        trigger.Sla.Should().Be(TimeSpan.FromHours(4));
    }

    [Fact]
    public void Thresholds_and_slas_are_configurable()
    {
        var strict = new CaseTriggerPolicy(new CaseTriggerOptions { NpsMediumAtOrBelow = 7, MediumSla = TimeSpan.FromHours(8) });

        var trigger = strict.Evaluate(new SurveyAnswers(7, null, null, null))!;

        trigger.Severity.Should().Be(CaseSeverity.Medium);
        trigger.Sla.Should().Be(TimeSpan.FromHours(8));
    }

    [Fact]
    public void Invalid_answers_never_reach_the_policy() =>
        FluentActions.Invoking(() => _policy.Evaluate(new SurveyAnswers(5, 6, null, null))).Should().Throw<ArgumentOutOfRangeException>();

    private static Arbitrary<SurveyAnswers> Answers() =>
        (from nps in Gen.Choose(0, 10)
         from csat in Gen.Choose(0, 5)
         from ces in Gen.Choose(0, 7)
         select new SurveyAnswers(nps, csat == 0 ? null : csat, ces == 0 ? null : ces, null)).ToArbitrary();

    [Property]
    public Property A_lower_nps_never_produces_a_lower_severity() =>
        Prop.ForAll(Answers(), a =>
        {
            if (a.Nps == 0)
            {
                return true;
            }

            var worse = a with { Nps = a.Nps - 1 };
            return Rank(_policy.Evaluate(worse)) >= Rank(_policy.Evaluate(a));
        });

    [Property]
    public Property A_case_opens_exactly_when_some_answer_crosses_a_threshold() =>
        Prop.ForAll(Answers(), a =>
        {
            var expected = a.Nps <= 6 || a.Csat <= 2 || a.Ces <= 2;
            return (_policy.Evaluate(a) is not null) == expected;
        });

    private static int Rank(CaseTrigger? t) => t is null ? 0 : (int)t.Severity;
}
