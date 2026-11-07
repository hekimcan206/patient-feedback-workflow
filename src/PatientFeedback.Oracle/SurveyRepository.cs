using Dapper;
using PatientFeedback.Cases;
using PatientFeedback.Dispatch;
using PatientFeedback.Privacy;
using PatientFeedback.Scoring;

namespace PatientFeedback.Oracle;

public sealed record SurveyPage
{
    public long SurveyId { get; init; }

    public string Department { get; init; } = "";

    public DateTimeOffset ExpiresAt { get; init; }
}

public enum SubmitStatus
{
    Accepted,

    /// <summary>Unknown, already used, expired or opted out. Deliberately one status: the page does not reveal which.</summary>
    NotAvailable,
}

/// <summary>A case that was just opened, with what the live panel needs (no personal data).</summary>
public sealed record OpenedCase(long CaseId, long SurveyId, string Department, CaseSeverity Severity, string Reasons, DateTimeOffset OpenedAt, DateTimeOffset SlaDueAt);

public sealed record SubmitResult(SubmitStatus Status, long? SurveyId = null, OpenedCase? Case = null, string? Department = null);

public sealed class SurveyRepository(OracleDb db, PiiProtector protector)
{
    private const string Actor = "patient";

    public async Task<SurveyPage?> FindOpenAsync(byte[] tokenHash, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<SurveyPage>(new CommandDefinition(
            """
            SELECT survey_id AS SurveyId, department AS Department, expires_at AS ExpiresAt
              FROM survey
             WHERE token_hash = :tokenHash AND status = 'SENT' AND expires_at > :now
            """, new { tokenHash, now }, cancellationToken: ct));
    }

    /// <summary>
    /// Records the answers and, if <paramref name="trigger"/> is set, opens the case — atomically with consuming the
    /// token. The conditional UPDATE is the single-use guarantee: of two concurrent submits exactly one matches.
    /// </summary>
    public async Task<SubmitResult> SubmitAsync(byte[] tokenHash, SurveyAnswers answers, CaseTrigger? trigger, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(answers);
        answers.Validate();
        await using var connection = await db.OpenAsync(ct);
        var target = await connection.QuerySingleOrDefaultAsync<SubmitTargetRow>(new CommandDefinition(
            """
            SELECT s.survey_id AS SurveyId, s.contact_id AS ContactId, s.department AS Department,
                   c.wrapped_dek AS WrappedDek, c.kek_id AS KekId
              FROM survey s
              JOIN contact c ON c.contact_id = s.contact_id
             WHERE s.token_hash = :tokenHash AND s.status = 'SENT' AND s.expires_at > :now AND c.anonymised_at IS NULL
            """, new { tokenHash, now }, cancellationToken: ct));
        if (target is null)
        {
            return new SubmitResult(SubmitStatus.NotAvailable);
        }

        byte[]? commentCt = null;
        var comment = string.IsNullOrWhiteSpace(answers.Comment) ? null : answers.Comment.Trim();
        if (comment is not null)
        {
            using var key = protector.Unwrap(new WrappedKey(target.KekId, target.WrappedDek), target.ContactId);
            commentCt = PiiProtector.Encrypt(key, target.ContactId, CommentField(target.SurveyId), comment);
        }

        await using var tx = await connection.BeginTransactionAsync(ct);
        var consumed = await connection.ExecuteAsync(
            """
            UPDATE survey
               SET status = 'COMPLETED', completed_at = :now, token_hash = NULL
             WHERE survey_id = :SurveyId AND token_hash = :tokenHash AND status = 'SENT' AND expires_at > :now
            """, new { now, target.SurveyId, tokenHash }, tx);
        if (consumed != 1)
        {
            await tx.RollbackAsync(ct);
            return new SubmitResult(SubmitStatus.NotAvailable);
        }

        await connection.ExecuteAsync(
            """
            INSERT INTO survey_response (survey_id, nps, csat, ces, comment_ct, submitted_at)
            VALUES (:SurveyId, :Nps, :Csat, :Ces, :CommentCt, :now)
            """, new { target.SurveyId, answers.Nps, answers.Csat, answers.Ces, CommentCt = commentCt, now }, tx);
        await connection.ExecuteAsync(
            "UPDATE contact SET last_activity_at = :now WHERE contact_id = :ContactId", new { now, target.ContactId }, tx);
        if (commentCt is not null)
        {
            await AuditRepository.WriteAsync(connection, tx,
                new AuditEntry(Actor, AuditActions.PiiWrite, "survey_response", target.SurveyId.ToString(System.Globalization.CultureInfo.InvariantCulture), "comment", "survey-response"));
        }

        OpenedCase? opened = null;
        if (trigger is not null)
        {
            opened = await CaseRepository.OpenAsync(connection, tx, target.SurveyId, target.ContactId, target.Department, trigger, now);
        }

        await tx.CommitAsync(ct);
        return new SubmitResult(SubmitStatus.Accepted, target.SurveyId, opened, target.Department);
    }

    /// <summary>"Do not send me surveys": opts the contact out of every channel and retires the link.</summary>
    public async Task<bool> OptOutAsync(byte[] tokenHash, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var target = await connection.QuerySingleOrDefaultAsync<SubmitTargetRow>(
            """
            SELECT survey_id AS SurveyId, contact_id AS ContactId, department AS Department
              FROM survey
             WHERE token_hash = :tokenHash AND status = 'SENT'
               FOR UPDATE
            """, new { tokenHash }, tx);
        if (target is null)
        {
            return false;
        }

        await connection.ExecuteAsync(
            "UPDATE survey SET status = 'OPTED_OUT', token_hash = NULL WHERE survey_id = :SurveyId", new { target.SurveyId }, tx);
        await ContactRepository.OptOutAsync(connection, tx, target.ContactId, [Channel.WhatsApp, Channel.Sms], Actor, "survey-link-opt-out", now);
        await tx.CommitAsync(ct);
        return true;
    }

    public static string CommentField(long surveyId) => ContactFields.Comment + ":" + surveyId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private sealed class SubmitTargetRow
    {
        public long SurveyId { get; init; }

        public string ContactId { get; init; } = "";

        public string Department { get; init; } = "";

        public byte[] WrappedDek { get; init; } = [];

        public string KekId { get; init; } = "";
    }
}
