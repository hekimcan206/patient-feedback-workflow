using PatientFeedback.Cases;
using PatientFeedback.Oracle;
using PatientFeedback.Scoring;
using PatientFeedback.Security;

namespace PatientFeedback.OracleTests;

/// <summary>retention_pkg against a real Oracle: periods from retention_policy, legal hold, idempotency, the scheduler job.</summary>
public sealed class RetentionPackageTests(OracleFixture fx) : IAsyncLifetime
{
    private RetentionRunner Runner => new(fx.OwnerConnectionString);

    public async ValueTask InitializeAsync() => await fx.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Contacts_past_365_days_are_anonymised_and_crypto_shredded_while_scores_survive()
    {
        var flow = new Flow(fx);
        var (token, surveyId, contactId) = await flow.SentSurveyAsync();
        SurveyTokens.TryHash(token, out var hash);
        await flow.Surveys.SubmitAsync(hash, new SurveyAnswers(10, 5, 7, "Çok memnun kaldım."), null, flow.Clock.Now);

        var day364 = await Runner.RunAsync(flow.Clock.Now.AddDays(364), "test");
        (await RunValue(day364, "contacts_anonymised")).Should().Be(0, "still inside the retention period");

        var day366 = await Runner.RunAsync(flow.Clock.Now.AddDays(366), "test");

        (await RunValue(day366, "contacts_anonymised")).Should().Be(1);
        (await fx.OwnerScalarAsync<int>(
            "SELECT COUNT(*) FROM contact WHERE contact_id = :contactId AND anonymised_at IS NOT NULL AND wrapped_dek IS NULL AND phone_ct IS NULL AND full_name_ct IS NULL AND patient_ref_bidx IS NULL AND phone_bidx IS NULL",
            new { contactId })).Should().Be(1);
        (await fx.OwnerScalarAsync<int>("SELECT nps FROM survey_response WHERE survey_id = :surveyId", new { surveyId })).Should().Be(10, "anonymous statistics are kept");
        (await fx.OwnerScalarAsync<int>("SELECT COUNT(*) FROM survey_response WHERE comment_ct IS NOT NULL")).Should().Be(0);
        (await flow.Contacts.RevealAsync(contactId, "agent", "test")).Should().BeNull("there is nothing left to reveal");
        (await fx.OwnerScalarAsync<string>("SELECT actor || '|' || purpose || '|' || db_user FROM audit_log WHERE action = 'PII_ANONYMISE'"))
            .Should().Be("RETENTION_JOB|kvkk-periodic-destruction|PF_OWNER");
        (await fx.OwnerScalarAsync<string>("SELECT status FROM retention_run WHERE run_id = :day366", new { day366 })).Should().Be("SUCCEEDED");
    }

    [Fact]
    public async Task An_unresolved_case_holds_the_contact_until_it_is_resolved()
    {
        var flow = new Flow(fx);
        var (token, _, contactId) = await flow.SentSurveyAsync();
        SurveyTokens.TryHash(token, out var hash);
        var answers = new SurveyAnswers(1, 1, 1, null);
        var opened = (await flow.Surveys.SubmitAsync(hash, answers, new CaseTriggerPolicy().Evaluate(answers), flow.Clock.Now)).Case!;

        var held = await Runner.RunAsync(flow.Clock.Now.AddDays(400), "test");
        (await RunValue(held, "held_open_case")).Should().Be(1);
        (await RunValue(held, "contacts_anonymised")).Should().Be(0);

        flow.Clock.Advance(TimeSpan.FromDays(400));
        await flow.Cases.ResolveAsync(opened.CaseId, "agent", "Geri arandı.", flow.Clock.Now);

        // Resolution restarts the clock: one year after the resolution, not after the visit.
        (await RunValue(await Runner.RunAsync(flow.Clock.Now.AddDays(200), "test"), "contacts_anonymised")).Should().Be(0);
        var later = await Runner.RunAsync(flow.Clock.Now.AddDays(366), "test");
        (await RunValue(later, "contacts_anonymised")).Should().Be(1);
        (await fx.OwnerScalarAsync<int>("SELECT COUNT(*) FROM case_event WHERE note_ct IS NOT NULL")).Should().Be(0);
        (await fx.OwnerScalarAsync<int>("SELECT COUNT(*) FROM contact WHERE contact_id = :contactId AND anonymised_at IS NOT NULL", new { contactId })).Should().Be(1);
    }

    [Fact]
    public async Task Expired_links_stop_working_and_their_queued_tokens_are_destroyed()
    {
        var flow = new Flow(fx);
        var (sentToken, sentSurvey, _) = await flow.SentSurveyAsync();
        var pending = await flow.Intake.RecordVisitAsync("evt-pending", flow.NewVisit());

        var run = await Runner.RunAsync(flow.Clock.Now.AddDays(8), "test");

        (await RunValue(run, "tokens_expired")).Should().Be(2);
        (await fx.OwnerScalarAsync<string>("SELECT status FROM survey WHERE survey_id = :sentSurvey", new { sentSurvey })).Should().Be("EXPIRED");
        (await fx.OwnerScalarAsync<string>("SELECT status FROM outbox_message WHERE survey_id = :id", new { id = pending.SurveyId })).Should().Be("CANCELLED");
        (await fx.OwnerScalarAsync<int>("SELECT COUNT(*) FROM outbox_message WHERE token_ct IS NOT NULL")).Should().Be(0);
        SurveyTokens.TryHash(sentToken, out var hash);
        (await flow.Surveys.FindOpenAsync(hash, flow.Clock.Now)).Should().BeNull();
    }

    [Fact]
    public async Task Comments_are_erased_after_180_days_while_the_contact_stays()
    {
        var flow = new Flow(fx);
        var (token, _, contactId) = await flow.SentSurveyAsync();
        SurveyTokens.TryHash(token, out var hash);
        await flow.Surveys.SubmitAsync(hash, new SurveyAnswers(8, 4, 6, "Temiz bir ortam."), null, flow.Clock.Now);

        var run = await Runner.RunAsync(flow.Clock.Now.AddDays(181), "test");

        (await RunValue(run, "comments_erased")).Should().Be(1);
        (await RunValue(run, "contacts_anonymised")).Should().Be(0);
        (await flow.Contacts.RevealAsync(contactId, "agent", "test")).Should().NotBeNull();
    }

    [Fact]
    public async Task Rerunning_is_idempotent_and_small_batches_process_everything()
    {
        var flow = new Flow(fx);
        for (var i = 0; i < 7; i++)
        {
            await flow.Intake.RecordVisitAsync("evt-b" + i, flow.NewVisit());
        }

        var first = await Runner.RunAsync(flow.Clock.Now.AddDays(400), "test", batchSize: 2);
        var second = await Runner.RunAsync(flow.Clock.Now.AddDays(400), "test", batchSize: 2);

        (await RunValue(first, "contacts_anonymised")).Should().Be(7, "four batches of two cover all seven");
        (await RunValue(second, "contacts_anonymised")).Should().Be(0);
        (await RunValue(second, "tokens_expired")).Should().Be(0);
    }

    [Fact]
    public async Task Audit_rows_older_than_730_days_are_deleted()
    {
        await fx.OwnerExecuteAsync(
            "INSERT INTO audit_log (at, actor, action, entity, entity_id, purpose) VALUES (SYS_EXTRACT_UTC(SYSTIMESTAMP) - INTERVAL '731' DAY(3), 'x', 'PII_READ', 'contact', 'c1', 'test')");
        await fx.OwnerExecuteAsync(
            "INSERT INTO audit_log (at, actor, action, entity, entity_id, purpose) VALUES (SYS_EXTRACT_UTC(SYSTIMESTAMP) - INTERVAL '10' DAY, 'x', 'PII_READ', 'contact', 'c2', 'test')");

        var run = await Runner.RunAsync(null, "test");

        (await RunValue(run, "audit_deleted")).Should().Be(1);
        (await fx.OwnerScalarAsync<string>("SELECT entity_id FROM audit_log")).Should().Be("c2");
    }

    [Fact]
    public async Task Nightly_job_is_registered_with_dbms_scheduler_and_runs_the_package()
    {
        (await fx.OwnerScalarAsync<string>("SELECT repeat_interval FROM user_scheduler_jobs WHERE job_name = 'PF_RETENTION_NIGHTLY'"))
            .Should().Be("FREQ=DAILY;BYHOUR=2;BYMINUTE=30");
        (await fx.OwnerScalarAsync<string>("SELECT job_action FROM user_scheduler_jobs WHERE job_name = 'PF_RETENTION_NIGHTLY'"))
            .Should().Be("RETENTION_PKG.RUN_SCHEDULED");

        await Runner.RunJobNowAsync();

        (await fx.OwnerScalarAsync<string>("SELECT status || '|' || triggered_by FROM retention_run ORDER BY run_id DESC FETCH FIRST 1 ROWS ONLY"))
            .Should().Be("SUCCEEDED|SCHEDULER");
    }

    [Fact]
    public async Task Enabling_the_job_schedules_its_next_run_within_a_day()
    {
        await fx.OwnerExecuteAsync("BEGIN retention_pkg.ensure_job(p_enabled => TRUE); END;");
        try
        {
            (await fx.OwnerScalarAsync<string>("SELECT enabled FROM user_scheduler_jobs WHERE job_name = 'PF_RETENTION_NIGHTLY'")).Should().Be("TRUE");
            var hoursToNext = await fx.OwnerScalarAsync<double>(
                "SELECT ROUND((CAST(next_run_date AS DATE) - CAST(SYSTIMESTAMP AS DATE)) * 24, 3) FROM user_scheduler_jobs WHERE job_name = 'PF_RETENTION_NIGHTLY'");
            hoursToNext.Should().BeInRange(0, 24, "a daily job is far inside KVKK's six-month periodic destruction ceiling");
        }
        finally
        {
            await fx.OwnerExecuteAsync("BEGIN retention_pkg.ensure_job(p_enabled => FALSE); END;");
        }
    }

    private Task<int> RunValue(long runId, string column) =>
        fx.OwnerScalarAsync<int>("SELECT " + column + " FROM retention_run WHERE run_id = :runId", new { runId });
}
