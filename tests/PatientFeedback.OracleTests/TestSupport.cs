using PatientFeedback.Dispatch;
using PatientFeedback.Domain;
using PatientFeedback.Oracle;
using PatientFeedback.Privacy;
using PatientFeedback.Synthetic;

namespace PatientFeedback.OracleTests;

/// <summary>A settable clock, so retention periods can be crossed without waiting a year.</summary>
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Drives the repositories through the same steps the web app takes, without HTTP.</summary>
public sealed class Flow
{
    private static int _seed = 1000;
    private readonly OracleFixture _fx;

    public Flow(OracleFixture fx, DateTimeOffset? start = null)
    {
        _fx = fx;
        Clock = new ManualClock(start ?? new DateTimeOffset(2026, 3, 2, 10, 0, 0, TimeSpan.Zero));
        Options = OracleFixture.ImmediateDispatch();
        Generator = new VisitGenerator(Interlocked.Increment(ref _seed));
        Intake = new IntakeRepository(fx.AppDb, fx.Protector, fx.BlindIndex, Options, Clock);
        Outbox = new OutboxRepository(fx.AppDb);
        Surveys = new SurveyRepository(fx.AppDb, fx.Protector);
        Cases = new CaseRepository(fx.AppDb, fx.Protector);
        Contacts = new ContactRepository(fx.AppDb, fx.Protector, fx.BlindIndex);
        Audit = new AuditRepository(fx.AppDb);
        Stats = new StatsRepository(fx.AppDb);
    }

    public ManualClock Clock { get; }

    public DispatchOptions Options { get; }

    public VisitGenerator Generator { get; }

    public IntakeRepository Intake { get; }

    public OutboxRepository Outbox { get; }

    public SurveyRepository Surveys { get; }

    public CaseRepository Cases { get; }

    public ContactRepository Contacts { get; }

    public AuditRepository Audit { get; }

    public StatsRepository Stats { get; }

    public VisitCompletedEvent NewVisit(bool consent = true, Channel channel = Channel.WhatsApp) =>
        Generator.Visit(Clock.Now.AddMinutes(-30), consentRate: consent ? 1 : 0) with { PreferredChannel = channel, SurveyConsent = consent };

    /// <summary>Visit → survey → claim → "provider accepted" → returns the link token the patient would receive.</summary>
    public async Task<(string Token, long SurveyId, string ContactId)> SentSurveyAsync(VisitCompletedEvent? visit = null)
    {
        visit ??= NewVisit();
        var result = await Intake.RecordVisitAsync("evt-" + Guid.NewGuid().ToString("N"), visit);
        result.Outcome.Should().Be(IntakeOutcome.SurveyScheduled);
        var (claimToken, messages) = await Outbox.ClaimAsync(100, TimeSpan.FromMinutes(1), Clock.Now);
        var message = messages.Single(m => m.SurveyId == result.SurveyId);
        using var key = _fx.Protector.Unwrap(new WrappedKey(message.KekId!, message.WrappedDek!), message.ContactId);
        var token = PiiProtector.Decrypt(key, message.ContactId, IntakeRepository.SurveyTokenField(message.SurveyId), message.TokenCt!);
        await Outbox.MarkSentAsync(message, claimToken, "test-provider-id", Clock.Now);
        // Other claimed rows (from earlier calls in the same test) go back to the queue.
        foreach (var other in messages.Where(m => m.MessageId != message.MessageId))
        {
            await Outbox.RetryAsync(other, claimToken, Clock.Now, "released by test");
        }

        return (token, message.SurveyId, message.ContactId);
    }
}
