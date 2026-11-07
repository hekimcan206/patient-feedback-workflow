using Dapper;
using Oracle.ManagedDataAccess.Client;
using PatientFeedback.Dispatch;
using PatientFeedback.Domain;
using PatientFeedback.Privacy;
using PatientFeedback.Security;

namespace PatientFeedback.Oracle;

public enum IntakeOutcome
{
    SurveyScheduled,
    NoConsent,
    DuplicateVisit,
    OptedOut,

    /// <summary>The webhook message id was already processed: a retry or a replay inside the tolerance window.</summary>
    DuplicateDelivery,
}

public sealed record IntakeResult(IntakeOutcome Outcome, long? SurveyId = null, DateTimeOffset? ScheduledFor = null);

/// <summary>
/// Turns a verified <c>visit.completed</c> event into a contact, a survey and an outbox message — in one transaction,
/// so a survey is never scheduled without its outbox row and no message is ever queued for a survey that rolled back.
/// </summary>
public sealed class IntakeRepository(OracleDb db, PiiProtector protector, BlindIndex blindIndex, DispatchOptions options, TimeProvider clock)
{
    private const string Actor = "his-webhook";
    private readonly DispatchSchedule _schedule = new(options);

    public async Task<IntakeResult> RecordVisitAsync(string eventId, VisitCompletedEvent visit, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        ArgumentNullException.ThrowIfNull(visit);

        // Two concurrent first visits of the same patient race on contact_bidx_ux; two deliveries of the same visit
        // with different message ids race on survey_visit_uk. The loser rolls back and re-runs once, then sees the
        // winner's committed rows and takes the normal "existing contact" / "duplicate visit" path.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await RecordOnceAsync(eventId, visit, ct);
            }
            catch (OracleException ex) when (ex.Number == 1 && OracleErrors.IsUniqueViolation(ex, "CLINICAL_EVENT_PK"))
            {
                return new IntakeResult(IntakeOutcome.DuplicateDelivery);
            }
            catch (OracleException ex) when (attempt == 1 && (OracleErrors.IsUniqueViolation(ex, "CONTACT_BIDX_UX") || OracleErrors.IsUniqueViolation(ex, "SURVEY_VISIT_UK")))
            {
            }
        }
    }

    private async Task<IntakeResult> RecordOnceAsync(string eventId, VisitCompletedEvent visit, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await using var connection = await db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        // Fast path for sender retries of an already committed delivery. The primary key remains the guarantee for
        // deliveries that are still in flight concurrently.
        if (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM clinical_event WHERE event_id = :eventId", new { eventId }, tx) > 0)
        {
            return new IntakeResult(IntakeOutcome.DuplicateDelivery);
        }

        if (!visit.SurveyConsent)
        {
            // Data minimisation: without consent nothing personal is stored, only that the event arrived.
            await InsertEventAsync(connection, tx, eventId, visit, null, now, "NO_CONSENT");
            await tx.CommitAsync(ct);
            return new IntakeResult(IntakeOutcome.NoConsent);
        }

        var contact = await UpsertContactAsync(connection, tx, visit, now, ct);
        try
        {
            if (contact.Route.Count == 0)
            {
                await InsertEventAsync(connection, tx, eventId, visit, contact.ContactId, now, "OPTED_OUT");
                await tx.CommitAsync(ct);
                return new IntakeResult(IntakeOutcome.OptedOut);
            }

            var surveyExists = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM survey WHERE visit_id = :VisitId", new { visit.VisitId }, tx);
            if (surveyExists > 0)
            {
                await InsertEventAsync(connection, tx, eventId, visit, contact.ContactId, now, "DUPLICATE_VISIT");
                await tx.CommitAsync(ct);
                return new IntakeResult(IntakeOutcome.DuplicateVisit);
            }

            await InsertEventAsync(connection, tx, eventId, visit, contact.ContactId, now, "SURVEY_SCHEDULED");

            var scheduledFor = _schedule.SendAt(visit.CompletedAt, now);
            var token = SurveyTokens.Issue();
            var survey = new DynamicParameters(new
            {
                EventId = eventId,
                visit.VisitId,
                contact.ContactId,
                visit.Department,
                TokenHash = token.Hash,
                CreatedAt = now,
                ScheduledFor = scheduledFor,
                ExpiresAt = scheduledFor + options.LinkLifetime,
            }).WithOutputId();
            await connection.ExecuteAsync(
                """
                INSERT INTO survey (event_id, visit_id, contact_id, department, token_hash, status, created_at, scheduled_for, expires_at)
                VALUES (:EventId, :VisitId, :ContactId, :Department, :TokenHash, 'SCHEDULED', :CreatedAt, :ScheduledFor, :ExpiresAt)
                RETURNING survey_id INTO :id
                """, survey, tx);
            var surveyId = survey.ReadId();

            // The link token is needed again only when the message is sent; until then it is kept encrypted under
            // the contact's data key, bound to this survey id.
            await connection.ExecuteAsync(
                """
                INSERT INTO outbox_message (survey_id, channel, status, token_ct, next_attempt_at, created_at)
                VALUES (:SurveyId, :Channel, 'PENDING', :TokenCt, :NextAttemptAt, :CreatedAt)
                """,
                new
                {
                    SurveyId = surveyId,
                    Channel = contact.Route[0].ToDb(),
                    TokenCt = PiiProtector.Encrypt(contact.Key, contact.ContactId, SurveyTokenField(surveyId), token.Value),
                    NextAttemptAt = scheduledFor,
                    CreatedAt = now,
                },
                tx);

            await tx.CommitAsync(ct);
            return new IntakeResult(IntakeOutcome.SurveyScheduled, surveyId, scheduledFor);
        }
        finally
        {
            contact.Key.Dispose();
        }
    }

    public static string SurveyTokenField(long surveyId) => "survey-token:" + surveyId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private async Task<ContactContext> UpsertContactAsync(OracleConnection connection, System.Data.Common.DbTransaction tx, VisitCompletedEvent visit, DateTimeOffset now, CancellationToken ct)
    {
        var patientBidx = blindIndex.Compute(visit.PatientRef);
        var phoneBidx = blindIndex.Compute(visit.Phone);
        var existing = await connection.QuerySingleOrDefaultAsync<ContactKeyRow>(new CommandDefinition(
            """
            SELECT contact_id AS ContactId, wrapped_dek AS WrappedDek, kek_id AS KekId,
                   whatsapp_opt_out_at AS WhatsAppOptOutAt, sms_opt_out_at AS SmsOptOutAt
              FROM contact
             WHERE patient_ref_bidx = :patientBidx
               FOR UPDATE
            """, new { patientBidx }, tx, cancellationToken: ct));

        string contactId;
        DataKey key;
        if (existing is null)
        {
            contactId = Guid.NewGuid().ToString("N");
            (key, var wrapped) = protector.CreateDataKey(contactId);
            await connection.ExecuteAsync(
                """
                INSERT INTO contact (contact_id, patient_ref_bidx, phone_bidx, full_name_ct, phone_ct, wrapped_dek, kek_id,
                                     preferred_channel, created_at, last_activity_at)
                VALUES (:ContactId, :PatientBidx, :PhoneBidx, :NameCt, :PhoneCt, :WrappedDek, :KekId, :Channel, :Now, :Now)
                """,
                new
                {
                    ContactId = contactId,
                    PatientBidx = patientBidx,
                    PhoneBidx = phoneBidx,
                    NameCt = PiiProtector.Encrypt(key, contactId, ContactFields.FullName, visit.PatientName),
                    PhoneCt = PiiProtector.Encrypt(key, contactId, ContactFields.Phone, visit.Phone),
                    WrappedDek = wrapped.Blob,
                    wrapped.KekId,
                    Channel = visit.PreferredChannel.ToDb(),
                    Now = now,
                },
                tx);
        }
        else
        {
            contactId = existing.ContactId;
            key = protector.Unwrap(new WrappedKey(existing.KekId, existing.WrappedDek), contactId);
            // Name and phone may have changed in the HIS since the last visit; overwrite without decrypting the old values.
            await connection.ExecuteAsync(
                """
                UPDATE contact
                   SET full_name_ct = :NameCt, phone_ct = :PhoneCt, phone_bidx = :PhoneBidx,
                       preferred_channel = :Channel, last_activity_at = :Now
                 WHERE contact_id = :ContactId
                """,
                new
                {
                    ContactId = contactId,
                    PhoneBidx = phoneBidx,
                    NameCt = PiiProtector.Encrypt(key, contactId, ContactFields.FullName, visit.PatientName),
                    PhoneCt = PiiProtector.Encrypt(key, contactId, ContactFields.Phone, visit.Phone),
                    Channel = visit.PreferredChannel.ToDb(),
                    Now = now,
                },
                tx);
        }

        await AuditRepository.WriteAsync(connection, tx,
            new AuditEntry(Actor, AuditActions.PiiWrite, "contact", contactId, "patient_ref,full_name,phone", "survey-intake"));

        var optedOut = new HashSet<Channel>();
        if (existing?.WhatsAppOptOutAt is not null)
        {
            optedOut.Add(Channel.WhatsApp);
        }

        if (existing?.SmsOptOutAt is not null)
        {
            optedOut.Add(Channel.Sms);
        }

        return new ContactContext(contactId, key, DispatchPlanner.Route(visit.PreferredChannel, optedOut));
    }

    private static Task<int> InsertEventAsync(OracleConnection connection, System.Data.Common.DbTransaction tx, string eventId, VisitCompletedEvent visit, string? contactId, DateTimeOffset now, string outcome) =>
        connection.ExecuteAsync(
            """
            INSERT INTO clinical_event (event_id, event_type, visit_id, contact_id, department, completed_at, received_at, outcome)
            VALUES (:EventId, :EventType, :VisitId, :ContactId, :Department, :CompletedAt, :ReceivedAt, :Outcome)
            """,
            new
            {
                EventId = eventId,
                EventType = visit.Type,
                visit.VisitId,
                ContactId = contactId,
                visit.Department,
                CompletedAt = visit.CompletedAt,
                ReceivedAt = now,
                Outcome = outcome,
            },
            tx);

    private sealed record ContactContext(string ContactId, DataKey Key, IReadOnlyList<Channel> Route);

    private sealed class ContactKeyRow
    {
        public string ContactId { get; init; } = "";

        public byte[] WrappedDek { get; init; } = [];

        public string KekId { get; init; } = "";

        public DateTimeOffset? WhatsAppOptOutAt { get; init; }

        public DateTimeOffset? SmsOptOutAt { get; init; }
    }
}

public static class ContactFields
{
    public const string FullName = "full_name";
    public const string Phone = "phone";
    public const string Comment = "comment";
    public const string Note = "note";
}
