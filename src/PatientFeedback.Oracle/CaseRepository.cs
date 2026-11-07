using System.Data;
using System.Globalization;
using Dapper;
using PatientFeedback.Cases;
using PatientFeedback.Privacy;

namespace PatientFeedback.Oracle;

/// <summary>A case as the panel lists it. No personal data: the contact is revealed separately, with an audit row.</summary>
public sealed class CaseRow
{
    public long CaseId { get; init; }

    public long SurveyId { get; init; }

    public string ContactId { get; init; } = "";

    public string Department { get; init; } = "";

    public int Severity { get; init; }

    public string Reasons { get; init; } = "";

    public string Status { get; init; } = "";

    public DateTimeOffset OpenedAt { get; init; }

    public DateTimeOffset SlaDueAt { get; init; }

    public string? AssignedTo { get; init; }

    public DateTimeOffset? ResolvedAt { get; init; }

    public string SlaBreached { get; init; } = "N";

    public int? Nps { get; init; }

    public int? Csat { get; init; }

    public int? Ces { get; init; }

    public int HasCommentFlag { get; init; }

    public int ContactAnonymisedFlag { get; init; }

    public bool HasComment => HasCommentFlag == 1;

    public bool ContactAnonymised => ContactAnonymisedFlag == 1;

    public bool IsBreached => SlaBreached == "Y";
}

public sealed record CaseEventView(DateTimeOffset At, string Actor, string Action, string? Note);

public sealed record CaseDetail(CaseRow Case, string? Comment, IReadOnlyList<CaseEventView> Events);

public sealed class CaseCounts
{
    public int Open { get; init; }

    public int InProgress { get; init; }

    public int Breached { get; init; }

    public int ResolvedToday { get; init; }
}

public sealed class CaseRepository(OracleDb db, PiiProtector protector)
{
    private const string SelectCase = """
        SELECT fc.case_id AS CaseId, fc.survey_id AS SurveyId, fc.contact_id AS ContactId, fc.department AS Department,
               fc.severity AS Severity, fc.reasons AS Reasons, fc.status AS Status, fc.opened_at AS OpenedAt,
               fc.sla_due_at AS SlaDueAt, fc.assigned_to AS AssignedTo, fc.resolved_at AS ResolvedAt,
               fc.sla_breached AS SlaBreached, r.nps AS Nps, r.csat AS Csat, r.ces AS Ces,
               CASE WHEN r.comment_ct IS NULL THEN 0 ELSE 1 END AS HasCommentFlag,
               CASE WHEN c.anonymised_at IS NULL THEN 0 ELSE 1 END AS ContactAnonymisedFlag
          FROM feedback_case fc
          JOIN contact c ON c.contact_id = fc.contact_id
          LEFT JOIN survey_response r ON r.survey_id = fc.survey_id
        """;

    internal static async Task<OpenedCase> OpenAsync(IDbConnection connection, IDbTransaction tx, long surveyId, string contactId, string department, CaseTrigger trigger, DateTimeOffset now)
    {
        var reasons = string.Join("; ", trigger.Reasons);
        if (reasons.Length > 400)
        {
            reasons = reasons[..400];
        }

        var slaDueAt = now + trigger.Sla;
        var p = new DynamicParameters(new
        {
            surveyId,
            contactId,
            department,
            severity = (int)trigger.Severity,
            reasons,
            now,
            slaDueAt,
        }).WithOutputId();
        await connection.ExecuteAsync(
            """
            INSERT INTO feedback_case (survey_id, contact_id, department, severity, reasons, status, opened_at, sla_due_at)
            VALUES (:surveyId, :contactId, :department, :severity, :reasons, 'OPEN', :now, :slaDueAt)
            RETURNING case_id INTO :id
            """, p, tx);
        var caseId = p.ReadId();
        await connection.ExecuteAsync(
            "INSERT INTO case_event (case_id, at, actor, action) VALUES (:caseId, :now, 'system', 'OPENED')",
            new { caseId, now }, tx);
        return new OpenedCase(caseId, surveyId, department, trigger.Severity, reasons, now, slaDueAt);
    }

    /// <summary>Unresolved cases ordered by SLA due time (served by feedback_case_open_ix), then the latest resolved.</summary>
    public async Task<IReadOnlyList<CaseRow>> ListAsync(int resolvedLimit, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var open = await connection.QueryAsync<CaseRow>(new CommandDefinition(
            SelectCase + " WHERE CASE WHEN fc.status <> 'RESOLVED' THEN fc.sla_due_at END IS NOT NULL ORDER BY CASE WHEN fc.status <> 'RESOLVED' THEN fc.sla_due_at END",
            cancellationToken: ct));
        var resolved = await connection.QueryAsync<CaseRow>(new CommandDefinition(
            SelectCase + " WHERE fc.status = 'RESOLVED' ORDER BY fc.resolved_at DESC FETCH FIRST :resolvedLimit ROWS ONLY",
            new { resolvedLimit }, cancellationToken: ct));
        return [.. open, .. resolved];
    }

    public async Task<CaseRow?> GetAsync(long caseId, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<CaseRow>(new CommandDefinition(SelectCase + " WHERE fc.case_id = :caseId", new { caseId }, cancellationToken: ct));
    }

    /// <summary>Case with decrypted comment and notes; reading them is a personal-data access and is audited first.</summary>
    public async Task<CaseDetail?> GetDetailAsync(long caseId, string actor, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<CaseRow>(new CommandDefinition(SelectCase + " WHERE fc.case_id = :caseId", new { caseId }, cancellationToken: ct));
        if (row is null)
        {
            return null;
        }

        var secrets = await connection.QuerySingleAsync<KeyAndComment>(new CommandDefinition(
            """
            SELECT c.wrapped_dek AS WrappedDek, c.kek_id AS KekId, r.comment_ct AS CommentCt
              FROM feedback_case fc
              JOIN contact c ON c.contact_id = fc.contact_id
              LEFT JOIN survey_response r ON r.survey_id = fc.survey_id
             WHERE fc.case_id = :caseId
            """, new { caseId }, cancellationToken: ct));
        var events = (await connection.QueryAsync<CaseEventRow>(new CommandDefinition(
            "SELECT at AS At, actor AS Actor, action AS Action, note_ct AS NoteCt FROM case_event WHERE case_id = :caseId ORDER BY at, case_event_id",
            new { caseId }, cancellationToken: ct))).ToList();

        var hasFreeText = secrets.CommentCt is not null || events.Any(e => e.NoteCt is not null);
        if (!hasFreeText || secrets.WrappedDek is null || secrets.KekId is null)
        {
            return new CaseDetail(row, null, [.. events.Select(e => new CaseEventView(e.At, e.Actor, e.Action, null))]);
        }

        await AuditRepository.WriteAsync(connection, null,
            new AuditEntry(actor, AuditActions.PiiRead, "feedback_case", Id(caseId), "comment,notes", "case-handling"));
        using var key = protector.Unwrap(new WrappedKey(secrets.KekId, secrets.WrappedDek), row.ContactId);
        var comment = secrets.CommentCt is null ? null : PiiProtector.Decrypt(key, row.ContactId, SurveyRepository.CommentField(row.SurveyId), secrets.CommentCt);
        return new CaseDetail(
            row,
            comment,
            [.. events.Select(e => new CaseEventView(e.At, e.Actor, e.Action, e.NoteCt is null ? null : PiiProtector.Decrypt(key, row.ContactId, NoteField(caseId), e.NoteCt)))]);
    }

    public async Task<bool> AssignAsync(long caseId, string actor, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var updated = await connection.ExecuteAsync(
            "UPDATE feedback_case SET status = 'IN_PROGRESS', assigned_to = :actor WHERE case_id = :caseId AND status <> 'RESOLVED'",
            new { actor, caseId }, tx);
        if (updated == 1)
        {
            await AddEventAsync(connection, tx, caseId, actor, "ASSIGNED", null, now);
        }

        await tx.CommitAsync(ct);
        return updated == 1;
    }

    public async Task<bool> AddNoteAsync(long caseId, string actor, string note, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var noteCt = await EncryptNoteAsync(connection, tx, caseId, note);
        if (noteCt is null)
        {
            return false;
        }

        await AddEventAsync(connection, tx, caseId, actor, "NOTE", noteCt, now);
        await AuditRepository.WriteAsync(connection, tx, new AuditEntry(actor, AuditActions.PiiWrite, "feedback_case", Id(caseId), "note", "case-handling"));
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> ResolveAsync(long caseId, string actor, string? note, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        byte[]? noteCt = null;
        if (!string.IsNullOrWhiteSpace(note))
        {
            noteCt = await EncryptNoteAsync(connection, tx, caseId, note);
            await AuditRepository.WriteAsync(connection, tx, new AuditEntry(actor, AuditActions.PiiWrite, "feedback_case", Id(caseId), "note", "case-handling"));
        }

        var updated = await connection.ExecuteAsync(
            "UPDATE feedback_case SET status = 'RESOLVED', resolved_at = :now, assigned_to = NVL(assigned_to, :actor) WHERE case_id = :caseId AND status <> 'RESOLVED'",
            new { now, actor, caseId }, tx);
        if (updated == 1)
        {
            await AddEventAsync(connection, tx, caseId, actor, "RESOLVED", noteCt, now);
            // Resolution is activity: the retention clock of the contact restarts here, and the legal hold lifts.
            await connection.ExecuteAsync(
                "UPDATE contact SET last_activity_at = :now WHERE contact_id = (SELECT contact_id FROM feedback_case WHERE case_id = :caseId) AND anonymised_at IS NULL",
                new { now, caseId }, tx);
        }

        await tx.CommitAsync(ct);
        return updated == 1;
    }

    /// <summary>Flags unresolved cases past their SLA. Returns the newly breached ones (each reported exactly once).</summary>
    public async Task<IReadOnlyList<long>> MarkBreachesAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var due = (await connection.QueryAsync<long>(new CommandDefinition(
            "SELECT case_id FROM feedback_case WHERE CASE WHEN status <> 'RESOLVED' THEN sla_due_at END < :now AND sla_breached = 'N'",
            new { now }, cancellationToken: ct))).ToList();
        var breached = new List<long>(due.Count);
        foreach (var caseId in due)
        {
            await using var tx = await connection.BeginTransactionAsync(ct);
            // Conditional update: with several app instances only one flips the flag and logs the event.
            var flipped = await connection.ExecuteAsync(
                "UPDATE feedback_case SET sla_breached = 'Y' WHERE case_id = :caseId AND sla_breached = 'N' AND status <> 'RESOLVED'",
                new { caseId }, tx);
            if (flipped == 1)
            {
                await AddEventAsync(connection, tx, caseId, "system", "SLA_BREACHED", null, now);
                breached.Add(caseId);
            }

            await tx.CommitAsync(ct);
        }

        return breached;
    }

    public async Task<CaseCounts> CountsAsync(DateTimeOffset startOfDay, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        return await connection.QuerySingleAsync<CaseCounts>(new CommandDefinition(
            """
            SELECT CAST(COUNT(CASE WHEN status = 'OPEN' THEN 1 END) AS NUMBER(10)) AS "Open",
                   CAST(COUNT(CASE WHEN status = 'IN_PROGRESS' THEN 1 END) AS NUMBER(10)) AS InProgress,
                   CAST(COUNT(CASE WHEN status <> 'RESOLVED' AND sla_breached = 'Y' THEN 1 END) AS NUMBER(10)) AS Breached,
                   CAST(COUNT(CASE WHEN status = 'RESOLVED' AND resolved_at >= :startOfDay THEN 1 END) AS NUMBER(10)) AS ResolvedToday
              FROM feedback_case
            """, new { startOfDay }, cancellationToken: ct));
    }

    public static string NoteField(long caseId) => ContactFields.Note + ":" + Id(caseId);

    private async Task<byte[]?> EncryptNoteAsync(IDbConnection connection, IDbTransaction tx, long caseId, string note)
    {
        if (note.Length > 400)
        {
            throw new ArgumentOutOfRangeException(nameof(note), "Notes are limited to 400 characters.");
        }

        var key = await connection.QuerySingleOrDefaultAsync<KeyAndComment>(
            """
            SELECT c.wrapped_dek AS WrappedDek, c.kek_id AS KekId, fc.contact_id AS ContactId
              FROM feedback_case fc JOIN contact c ON c.contact_id = fc.contact_id
             WHERE fc.case_id = :caseId AND c.anonymised_at IS NULL
            """, new { caseId }, tx);
        if (key?.WrappedDek is null || key.KekId is null)
        {
            return null;
        }

        using var dek = protector.Unwrap(new WrappedKey(key.KekId, key.WrappedDek), key.ContactId!);
        return PiiProtector.Encrypt(dek, key.ContactId!, NoteField(caseId), note.Trim());
    }

    private static Task<int> AddEventAsync(IDbConnection connection, IDbTransaction tx, long caseId, string actor, string action, byte[]? noteCt, DateTimeOffset now) =>
        connection.ExecuteAsync(
            "INSERT INTO case_event (case_id, at, actor, action, note_ct) VALUES (:caseId, :now, :actor, :action, :noteCt)",
            new { caseId, now, actor, action, noteCt }, tx);

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    private sealed class KeyAndComment
    {
        public byte[]? WrappedDek { get; init; }

        public string? KekId { get; init; }

        public byte[]? CommentCt { get; init; }

        public string? ContactId { get; init; }
    }

    private sealed class CaseEventRow
    {
        public DateTimeOffset At { get; init; }

        public string Actor { get; init; } = "";

        public string Action { get; init; } = "";

        public byte[]? NoteCt { get; init; }
    }
}
