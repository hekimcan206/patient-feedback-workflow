using Dapper;
using PatientFeedback.Scoring;

namespace PatientFeedback.Oracle;

public sealed class ScoreCountRow
{
    public string Department { get; init; } = "";

    public int Nps { get; init; }

    public int? Csat { get; init; }

    public int? Ces { get; init; }

    public int Responses { get; init; }
}

public sealed class LatencyRow
{
    public int Messages { get; init; }

    public double? P50Ms { get; init; }

    public double? P95Ms { get; init; }

    public double? P99Ms { get; init; }

    public double? MaxMs { get; init; }
}

public sealed class FunnelRow
{
    public string Status { get; init; } = "";

    public int Surveys { get; init; }
}

public sealed class RetentionRunRow
{
    public long RunId { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }

    public DateTimeOffset AsOf { get; init; }

    public string TriggeredBy { get; init; } = "";

    public int TokensExpired { get; init; }

    public int CommentsErased { get; init; }

    public int NotesErased { get; init; }

    public int ContactsAnonymised { get; init; }

    public int HeldOpenCase { get; init; }

    public int AuditDeleted { get; init; }

    public string Status { get; init; } = "";

    public string? ErrorMessage { get; init; }
}

public sealed record DepartmentScores(string Department, ScoreSummary Scores);

public sealed class StatsRepository(OracleDb db)
{
    /// <summary>
    /// Scores per department since <paramref name="since"/>. Oracle groups to (department, nps, csat, ces) counts —
    /// at most 11×6×8 rows per department however many responses — and the scoring services compute the metrics, so
    /// the formulas exist once, in tested C#.
    /// </summary>
    public async Task<(ScoreSummary Overall, IReadOnlyList<DepartmentScores> ByDepartment)> ScoresAsync(DateTimeOffset since, ScoringService scoring, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scoring);
        await using var connection = await db.OpenAsync(ct);
        var rows = (await connection.QueryAsync<ScoreCountRow>(new CommandDefinition(
            """
            SELECT s.department AS Department, r.nps AS Nps, r.csat AS Csat, r.ces AS Ces, COUNT(*) AS Responses
              FROM survey_response r
              JOIN survey s ON s.survey_id = r.survey_id
             WHERE r.submitted_at >= :since
             GROUP BY s.department, r.nps, r.csat, r.ces
            """, new { since }, cancellationToken: ct))).ToList();

        static IEnumerable<SurveyAnswers> Expand(IEnumerable<ScoreCountRow> source) =>
            source.SelectMany(r => Enumerable.Repeat(new SurveyAnswers(r.Nps, r.Csat, r.Ces, null), r.Responses));

        var overall = scoring.Summarise([.. Expand(rows)]);
        var byDepartment = rows.GroupBy(r => r.Department, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new DepartmentScores(g.Key, scoring.Summarise([.. Expand(g)])))
            .ToList();
        return (overall, byDepartment);
    }

    /// <summary>Webhook received → provider accepted, in milliseconds, computed in Oracle with PERCENTILE_CONT.</summary>
    public async Task<LatencyRow> DispatchLatencyAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        return await connection.QuerySingleAsync<LatencyRow>(new CommandDefinition(
            """
            WITH d AS (
                SELECT EXTRACT(DAY FROM (o.sent_at - e.received_at)) * 86400000
                     + EXTRACT(HOUR FROM (o.sent_at - e.received_at)) * 3600000
                     + EXTRACT(MINUTE FROM (o.sent_at - e.received_at)) * 60000
                     + EXTRACT(SECOND FROM (o.sent_at - e.received_at)) * 1000 AS ms
                  FROM outbox_message o
                  JOIN survey s ON s.survey_id = o.survey_id
                  JOIN clinical_event e ON e.event_id = s.event_id
                 WHERE o.status = 'SENT' AND e.received_at >= :since
            )
            SELECT COUNT(*) AS Messages,
                   PERCENTILE_CONT(0.50) WITHIN GROUP (ORDER BY ms) AS P50Ms,
                   PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY ms) AS P95Ms,
                   PERCENTILE_CONT(0.99) WITHIN GROUP (ORDER BY ms) AS P99Ms,
                   MAX(ms) AS MaxMs
              FROM d
            """, new { since }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<FunnelRow>> FunnelAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var rows = await connection.QueryAsync<FunnelRow>(new CommandDefinition(
            "SELECT status AS Status, COUNT(*) AS Surveys FROM survey WHERE created_at >= :since GROUP BY status ORDER BY status",
            new { since }, cancellationToken: ct));
        return [.. rows];
    }

    public async Task<IReadOnlyList<RetentionRunRow>> RetentionRunsAsync(int limit, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var rows = await connection.QueryAsync<RetentionRunRow>(new CommandDefinition(
            """
            SELECT run_id AS RunId, started_at AS StartedAt, finished_at AS FinishedAt, as_of AS AsOf, triggered_by AS TriggeredBy,
                   tokens_expired AS TokensExpired, comments_erased AS CommentsErased, notes_erased AS NotesErased,
                   contacts_anonymised AS ContactsAnonymised, held_open_case AS HeldOpenCase, audit_deleted AS AuditDeleted,
                   status AS Status, error_message AS ErrorMessage
              FROM retention_run
             ORDER BY run_id DESC
             FETCH FIRST :limit ROWS ONLY
            """, new { limit }, cancellationToken: ct));
        return [.. rows];
    }
}
