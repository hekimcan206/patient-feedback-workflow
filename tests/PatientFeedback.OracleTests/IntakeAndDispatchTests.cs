using System.Text;
using Oracle.ManagedDataAccess.Client;
using PatientFeedback.Dispatch;
using PatientFeedback.Oracle;
using PatientFeedback.Security;

namespace PatientFeedback.OracleTests;

public sealed class IntakeAndDispatchTests(OracleFixture fx) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fx.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Visit_creates_contact_survey_and_outbox_row_in_one_transaction_with_pii_encrypted()
    {
        var flow = new Flow(fx);
        var visit = flow.NewVisit();

        var result = await flow.Intake.RecordVisitAsync("evt-1", visit);

        result.Outcome.Should().Be(IntakeOutcome.SurveyScheduled);
        (await fx.OwnerScalarAsync<int>("SELECT COUNT(*) FROM survey WHERE survey_id = :id AND status = 'SCHEDULED'", new { id = result.SurveyId })).Should().Be(1);
        (await fx.OwnerScalarAsync<int>("SELECT COUNT(*) FROM outbox_message WHERE survey_id = :id AND status = 'PENDING' AND channel = 'WHATSAPP'", new { id = result.SurveyId })).Should().Be(1);

        // Nothing readable at rest: neither name nor phone appears in any stored bytes.
        var nameCt = await fx.OwnerScalarAsync<byte[]>("SELECT full_name_ct FROM contact");
        var phoneCt = await fx.OwnerScalarAsync<byte[]>("SELECT phone_ct FROM contact");
        Encoding.UTF8.GetString(nameCt).Should().NotContain(visit.PatientName.Split(' ')[0]);
        Encoding.UTF8.GetString(phoneCt).Should().NotContain(visit.Phone[3..]);
        (await fx.OwnerScalarAsync<string>("SELECT kek_id FROM contact")).Should().Be("kek-test-1");

        var audit = await fx.OwnerScalarAsync<string>("SELECT action || '|' || actor || '|' || purpose || '|' || db_user FROM audit_log WHERE entity = 'contact'");
        audit.Should().Be("PII_WRITE|his-webhook|survey-intake|PF_APP");
    }

    [Fact]
    public async Task Redelivered_message_id_is_acknowledged_without_processing_again()
    {
        var flow = new Flow(fx);
        var visit = flow.NewVisit();

        (await flow.Intake.RecordVisitAsync("evt-dup", visit)).Outcome.Should().Be(IntakeOutcome.SurveyScheduled);
        (await flow.Intake.RecordVisitAsync("evt-dup", visit)).Outcome.Should().Be(IntakeOutcome.DuplicateDelivery);

        (await fx.OwnerScalarAsync<int>("SELECT COUNT(*) FROM survey")).Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_deliveries_of_the_same_message_schedule_exactly_one_survey()
    {
        var flow = new Flow(fx);
        var visit = flow.NewVisit();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => flow.Intake.RecordVisitAsync("evt-race", visit))));

        results.Count(r => r.Outcome == IntakeOutcome.SurveyScheduled).Should().Be(1);
        results.Count(r => r.Outcome == IntakeOutcome.DuplicateDelivery).Should().Be(7);
        (await fx.OwnerScalarAsync<int>("SELECT COUNT(*) FROM outbox_message")).Should().Be(1);
    }

    [Fact]
    public async Task Same_visit_under_a_new_message_id_does_not_send_a_second_survey()
    {
        var flow = new Flow(fx);
        var visit = flow.NewVisit();

        await flow.Intake.RecordVisitAsync("evt-a", visit);
        var second = await flow.Intake.RecordVisitAsync("evt-b", visit);

        second.Outcome.Should().Be(IntakeOutcome.DuplicateVisit);
        (await fx.OwnerScalarAsync<int>("SELECT COUNT(*) FROM survey")).Should().Be(1);
        (await fx.OwnerScalarAsync<int>("SELECT COUNT(*) FROM clinical_event")).Should().Be(2);
    }

    [Fact]
    public async Task Without_consent_no_personal_data_is_stored()
    {
        var flow = new Flow(fx);

        var result = await flow.Intake.RecordVisitAsync("evt-nc", flow.NewVisit(consent: false));

        result.Outcome.Should().Be(IntakeOutcome.NoConsent);
        (await fx.OwnerScalarAsync<int>("SELECT COUNT(*) FROM contact")).Should().Be(0);
        (await fx.OwnerScalarAsync<string>("SELECT outcome FROM clinical_event")).Should().Be("NO_CONSENT");
    }

    [Fact]
    public async Task Returning_patient_reuses_the_contact_and_its_data_key()
    {
        var flow = new Flow(fx);
        var first = flow.NewVisit();
        var second = flow.NewVisit() with { PatientRef = first.PatientRef, Phone = "+905320009999" };

        await flow.Intake.RecordVisitAsync("evt-r1", first);
        await flow.Intake.RecordVisitAsync("evt-r2", second);

        (await fx.OwnerScalarAsync<int>("SELECT COUNT(*) FROM contact")).Should().Be(1);
        var contactId = await fx.OwnerScalarAsync<string>("SELECT contact_id FROM contact");
        var revealed = await flow.Contacts.RevealAsync(contactId, "tester", "test");
        revealed!.Phone.Should().Be("+905320009999", "the latest phone from the HIS wins");
    }

    [Fact]
    public async Task Competing_dispatchers_claim_disjoint_batches()
    {
        var flow = new Flow(fx);
        for (var i = 0; i < 40; i++)
        {
            await flow.Intake.RecordVisitAsync("evt-c" + i, flow.NewVisit());
        }

        var claims = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => flow.Outbox.ClaimAsync(15, TimeSpan.FromMinutes(1), flow.Clock.Now))));

        var ids = claims.SelectMany(c => c.Messages.Select(m => m.MessageId)).ToList();
        ids.Should().OnlyHaveUniqueItems();
        ids.Should().HaveCount(40, "four claimers of 15 can take all 40 rows, and none twice");
    }

    [Fact]
    public async Task Expired_lease_is_reclaimed_and_the_stale_worker_cannot_overwrite_the_new_state()
    {
        var flow = new Flow(fx);
        await flow.Intake.RecordVisitAsync("evt-lease", flow.NewVisit());

        var (staleToken, first) = await flow.Outbox.ClaimAsync(10, TimeSpan.FromSeconds(30), flow.Clock.Now);
        first.Should().ContainSingle();
        (await flow.Outbox.ClaimAsync(10, TimeSpan.FromSeconds(30), flow.Clock.Now)).Messages.Should().BeEmpty("the lease is still held");

        flow.Clock.Advance(TimeSpan.FromSeconds(31));
        var (freshToken, again) = await flow.Outbox.ClaimAsync(10, TimeSpan.FromSeconds(30), flow.Clock.Now);
        again.Should().ContainSingle().Which.MessageId.Should().Be(first[0].MessageId);

        await flow.Outbox.MarkSentAsync(first[0], staleToken, "late-provider-id", flow.Clock.Now);
        (await fx.OwnerScalarAsync<string>("SELECT status FROM outbox_message")).Should().Be("IN_FLIGHT", "the stale worker's write is fenced out");

        await flow.Outbox.MarkSentAsync(again[0], freshToken, "provider-id", flow.Clock.Now);
        (await fx.OwnerScalarAsync<string>("SELECT status || ':' || provider_message_id FROM outbox_message")).Should().Be("SENT:provider-id");
        (await fx.OwnerScalarAsync<int>("SELECT COUNT(*) FROM outbox_message WHERE token_ct IS NULL")).Should().Be(1, "the queued token is destroyed once sent");
    }

    [Fact]
    public async Task Inbound_stop_reply_opts_out_by_phone_blind_index()
    {
        var flow = new Flow(fx);
        var visit = flow.NewVisit();
        await flow.Intake.RecordVisitAsync("evt-o1", visit);

        (await flow.Contacts.OptOutByPhoneAsync(visit.Phone, Channel.WhatsApp, "provider:whatsapp", flow.Clock.Now)).Should().Be(1);
        (await flow.Contacts.OptOutByPhoneAsync(visit.Phone, Channel.Sms, "provider:sms", flow.Clock.Now)).Should().Be(1);

        var next = flow.NewVisit() with { PatientRef = visit.PatientRef, Phone = visit.Phone };
        (await flow.Intake.RecordVisitAsync("evt-o2", next)).Outcome.Should().Be(IntakeOutcome.OptedOut);
        (await fx.OwnerScalarAsync<int>("SELECT COUNT(*) FROM audit_log WHERE action = 'OPT_OUT'")).Should().Be(2);
    }

    [Fact]
    public async Task Survey_link_works_exactly_once_under_concurrent_submits()
    {
        var flow = new Flow(fx);
        var (token, _, _) = await flow.SentSurveyAsync();
        SurveyTokens.TryHash(token, out var hash).Should().BeTrue();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            Task.Run(() => flow.Surveys.SubmitAsync(hash, new Scoring.SurveyAnswers(9, 5, 7, null), null, flow.Clock.Now))));

        results.Count(r => r.Status == SubmitStatus.Accepted).Should().Be(1);
        (await fx.OwnerScalarAsync<int>("SELECT COUNT(*) FROM survey_response")).Should().Be(1);
        (await flow.Surveys.FindOpenAsync(hash, flow.Clock.Now)).Should().BeNull("a used link no longer opens");
    }

    [Fact]
    public async Task Low_score_opens_a_case_with_sla_and_encrypted_comment()
    {
        var flow = new Flow(fx);
        var (token, surveyId, _) = await flow.SentSurveyAsync();
        SurveyTokens.TryHash(token, out var hash);
        var answers = new Scoring.SurveyAnswers(2, 1, 2, "Bekleme süresi çok uzundu.");
        var trigger = new Cases.CaseTriggerPolicy().Evaluate(answers);

        var result = await flow.Surveys.SubmitAsync(hash, answers, trigger, flow.Clock.Now);

        result.Case.Should().NotBeNull();
        result.Case!.Severity.Should().Be(Cases.CaseSeverity.High);
        result.Case.SlaDueAt.Should().Be(flow.Clock.Now.AddHours(4));
        var detail = await flow.Cases.GetDetailAsync(result.Case.CaseId, "agent.ayse");
        detail!.Comment.Should().Be("Bekleme süresi çok uzundu.");
        detail.Case.SurveyId.Should().Be(surveyId);
        (await fx.OwnerScalarAsync<int>("SELECT COUNT(*) FROM audit_log WHERE action = 'PII_READ' AND actor = 'agent.ayse'")).Should().Be(1);
    }

    [Fact]
    public async Task Case_lifecycle_assign_note_resolve_and_sla_breach_once()
    {
        var flow = new Flow(fx);
        var (token, _, _) = await flow.SentSurveyAsync();
        SurveyTokens.TryHash(token, out var hash);
        var answers = new Scoring.SurveyAnswers(5, 3, 4, null);
        var opened = (await flow.Surveys.SubmitAsync(hash, answers, new Cases.CaseTriggerPolicy().Evaluate(answers), flow.Clock.Now)).Case!;

        flow.Clock.Advance(TimeSpan.FromHours(25));
        (await flow.Cases.MarkBreachesAsync(flow.Clock.Now)).Should().Equal(opened.CaseId);
        (await flow.Cases.MarkBreachesAsync(flow.Clock.Now)).Should().BeEmpty("a breach is reported once");

        (await flow.Cases.AssignAsync(opened.CaseId, "agent.mehmet", flow.Clock.Now)).Should().BeTrue();
        (await flow.Cases.AddNoteAsync(opened.CaseId, "agent.mehmet", "Hasta arandı, özür dilendi.", flow.Clock.Now)).Should().BeTrue();
        (await flow.Cases.ResolveAsync(opened.CaseId, "agent.mehmet", "Randevu yeniden planlandı.", flow.Clock.Now)).Should().BeTrue();

        var detail = await flow.Cases.GetDetailAsync(opened.CaseId, "agent.mehmet");
        detail!.Events.Select(e => e.Action).Should().Equal("OPENED", "SLA_BREACHED", "ASSIGNED", "NOTE", "RESOLVED");
        detail.Events[3].Note.Should().Be("Hasta arandı, özür dilendi.");
        detail.Case.Status.Should().Be("RESOLVED");
        (await fx.OwnerScalarAsync<int>("SELECT COUNT(*) FROM case_event WHERE note_ct IS NOT NULL")).Should().Be(2);
    }

    [Fact]
    public async Task Kek_rotation_rewraps_data_keys_without_touching_field_ciphertext()
    {
        var flow = new Flow(fx);
        await flow.Intake.RecordVisitAsync("evt-k", flow.NewVisit());
        var phoneBefore = await fx.OwnerScalarAsync<byte[]>("SELECT phone_ct FROM contact");
        var oldKek = new Privacy.KeyEncryptionKey("kek-test-1", OracleFixture.KekBytes);
        var newKek = new Privacy.KeyEncryptionKey("kek-test-2", System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var rotated = new ContactRepository(fx.AppDb, new Privacy.PiiProtector([oldKek, newKek], "kek-test-2"), fx.BlindIndex);

        (await rotated.RewrapKeysAsync(10)).Should().Be(1);

        (await fx.OwnerScalarAsync<string>("SELECT kek_id FROM contact")).Should().Be("kek-test-2");
        (await fx.OwnerScalarAsync<byte[]>("SELECT phone_ct FROM contact")).Should().Equal(phoneBefore);
        var contactId = await fx.OwnerScalarAsync<string>("SELECT contact_id FROM contact");
        (await rotated.RevealAsync(contactId, "tester", "test")).Should().NotBeNull();
    }

    [Fact]
    public async Task Application_account_cannot_alter_the_audit_trail_or_run_retention()
    {
        await using var connection = await fx.AppDb.OpenAsync();
        await using var cmd = connection.CreateCommand();

        foreach (var sql in new[]
                 {
                     "DELETE FROM audit_log",
                     "UPDATE audit_log SET actor = 'x'",
                     "DELETE FROM contact",
                     "BEGIN retention_pkg.ensure_job; END;",
                 })
        {
            cmd.CommandText = sql;
            var act = () => cmd.ExecuteNonQueryAsync();
            // ORA-01031 / ORA-41900 (23ai: missing object privilege), ORA-00942/ORA-06550 object not visible to this account.
            (await act.Should().ThrowAsync<OracleException>()).Which.Number.Should().BeOneOf(1031, 41900, 942, 6550);
        }
    }

    [Fact]
    public async Task Open_case_panel_query_is_served_by_the_partial_index()
    {
        await fx.OwnerExecuteAsync("BEGIN DBMS_STATS.GATHER_TABLE_STATS(USER, 'FEEDBACK_CASE'); END;");
        await fx.OwnerExecuteAsync("EXPLAIN PLAN SET STATEMENT_ID = 'open_cases' FOR SELECT case_id FROM feedback_case WHERE CASE WHEN status <> 'RESOLVED' THEN sla_due_at END IS NOT NULL ORDER BY CASE WHEN status <> 'RESOLVED' THEN sla_due_at END");

        var plan = await fx.OwnerScalarAsync<string>("SELECT LISTAGG(operation || ' ' || options || ' ' || object_name, ' | ') WITHIN GROUP (ORDER BY id) FROM plan_table WHERE statement_id = 'open_cases'");

        plan.Should().Contain("FEEDBACK_CASE_OPEN_IX");
    }
}
