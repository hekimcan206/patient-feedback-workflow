using System.Data;
using System.Diagnostics;
using Dapper;
using Oracle.ManagedDataAccess.Client;

namespace PatientFeedback.Oracle;

public static class AuditActions
{
    public const string PiiWrite = "PII_WRITE";
    public const string PiiRead = "PII_READ";
    public const string PiiReveal = "PII_REVEAL";
    public const string OptOut = "OPT_OUT";
}

/// <summary>One access to personal data: who, what, which record, which fields, why.</summary>
public sealed record AuditEntry(string Actor, string Action, string Entity, string EntityId, string Fields, string Purpose);

public sealed class AuditRow
{
    public long AuditId { get; init; }

    public DateTimeOffset At { get; init; }

    public string Actor { get; init; } = "";

    public string Action { get; init; } = "";

    public string Entity { get; init; } = "";

    public string EntityId { get; init; } = "";

    public string? Fields { get; init; }

    public string Purpose { get; init; } = "";

    public string? TraceId { get; init; }

    public string DbUser { get; init; } = "";
}

/// <summary>
/// Writes the personal-data access trail. Writes take the caller's connection and transaction so the audit row
/// commits or rolls back together with the access it describes: there is no "read happened, audit lost" window.
/// </summary>
public sealed class AuditRepository(OracleDb db)
{
    private const string InsertSql = """
        INSERT INTO audit_log (actor, action, entity, entity_id, fields, purpose, trace_id)
        VALUES (:Actor, :Action, :Entity, :EntityId, :Fields, :Purpose, :TraceId)
        """;

    public static Task WriteAsync(IDbConnection connection, IDbTransaction? transaction, AuditEntry entry) =>
        WriteAsync(connection, transaction, [entry]);

    public static async Task WriteAsync(IDbConnection connection, IDbTransaction? transaction, IReadOnlyCollection<AuditEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var traceId = Activity.Current?.TraceId.ToString();
        if (entries.Count > 1 && connection is OracleConnection oracle)
        {
            // ODP.NET array binding: one round trip for the whole batch. Dapper would execute once per element,
            // which made auditing a 32-message dispatch batch cost 32 round trips (see docs/benchmarks.md).
            await using var command = oracle.CreateCommand();
            command.CommandText = InsertSql;
            command.BindByName = true;
            command.ArrayBindCount = entries.Count;
            command.Transaction = (OracleTransaction?)transaction;
            command.Parameters.Add(Array("Actor", entries.Select(e => e.Actor)));
            command.Parameters.Add(Array("Action", entries.Select(e => e.Action)));
            command.Parameters.Add(Array("Entity", entries.Select(e => e.Entity)));
            command.Parameters.Add(Array("EntityId", entries.Select(e => e.EntityId)));
            command.Parameters.Add(Array("Fields", entries.Select(e => e.Fields)));
            command.Parameters.Add(Array("Purpose", entries.Select(e => e.Purpose)));
            command.Parameters.Add(Array("TraceId", entries.Select(_ => traceId)));
            await command.ExecuteNonQueryAsync();
            return;
        }

        await connection.ExecuteAsync(InsertSql, entries.Select(e => new
        {
            e.Actor,
            e.Action,
            e.Entity,
            e.EntityId,
            e.Fields,
            e.Purpose,
            TraceId = traceId,
        }), transaction);
    }

    private static OracleParameter Array(string name, IEnumerable<string?> values) =>
        new(name, OracleDbType.Varchar2) { Value = values.Select(v => (object?)v ?? DBNull.Value).ToArray() };

    public async Task WriteAsync(AuditEntry entry, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        await WriteAsync(connection, null, entry);
    }

    public async Task<IReadOnlyList<AuditRow>> RecentAsync(int limit, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var rows = await connection.QueryAsync<AuditRow>(new CommandDefinition(
            """
            SELECT audit_id AS AuditId, at AS At, actor AS Actor, action AS Action, entity AS Entity, entity_id AS EntityId,
                   fields AS Fields, purpose AS Purpose, trace_id AS TraceId, db_user AS DbUser
              FROM audit_log
             ORDER BY audit_id DESC
             FETCH FIRST :limit ROWS ONLY
            """, new { limit }, cancellationToken: ct));
        return [.. rows];
    }

    public async Task<IReadOnlyList<AuditRow>> ForEntityAsync(string entity, string entityId, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var rows = await connection.QueryAsync<AuditRow>(new CommandDefinition(
            """
            SELECT audit_id AS AuditId, at AS At, actor AS Actor, action AS Action, entity AS Entity, entity_id AS EntityId,
                   fields AS Fields, purpose AS Purpose, trace_id AS TraceId, db_user AS DbUser
              FROM audit_log
             WHERE entity = :entity AND entity_id = :entityId
             ORDER BY audit_id
            """, new { entity, entityId }, cancellationToken: ct));
        return [.. rows];
    }
}
