using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Oracle.ManagedDataAccess.Client;

namespace PatientFeedback.Oracle;

/// <summary>
/// Applies the embedded schema scripts as the schema owner and (re)creates the retention job. Safe to run on every
/// start: tables are created only on an empty schema; packages (CREATE OR REPLACE), grants and the job are reapplied.
/// </summary>
public sealed partial class OracleSchemaInstaller(string ownerConnectionString)
{
    private static readonly string[] Packages = ["02-outbox-pkg.sql", "03-retention-pkg.sql"];

    public async Task InstallAsync(string appUser, RetentionJobOptions? job = null, CancellationToken cancellationToken = default)
    {
        var grantee = OracleNames.Identifier(appUser);
        job ??= new RetentionJobOptions();
        await using var connection = new OracleConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);

        if (!await TableExistsAsync(connection, "CONTACT", cancellationToken))
        {
            await RunScriptAsync(connection, "01-tables.sql", grantee, cancellationToken);
        }

        foreach (var script in Packages)
        {
            await RunScriptAsync(connection, script, grantee, cancellationToken);
        }

        await AssertValidAsync(connection, cancellationToken);
        await RunScriptAsync(connection, "04-grants.sql", grantee, cancellationToken);

        await using var ensureJob = connection.CreateCommand();
        ensureJob.CommandText = "BEGIN retention_pkg.ensure_job(p_repeat_interval => :interval, p_enabled => " + (job.Enabled ? "TRUE" : "FALSE") + "); END;";
        ensureJob.Parameters.Add(new OracleParameter("interval", job.RepeatInterval));
        await ensureJob.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RunScriptAsync(OracleConnection connection, string script, string grantee, CancellationToken ct)
    {
        foreach (var statement in Split(ReadScript(script)))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = statement.Replace("${APP_USER}", grantee, StringComparison.Ordinal);
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<bool> TableExistsAsync(OracleConnection connection, string table, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM user_tables WHERE table_name = :t";
        command.Parameters.Add(new OracleParameter("t", table));
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0;
    }

    private static async Task AssertValidAsync(OracleConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT LISTAGG(name || ' ' || type || ' line ' || line || ': ' || text, '; ') WITHIN GROUP (ORDER BY name, sequence)
              FROM user_errors
             WHERE name IN ('OUTBOX_PKG', 'RETENTION_PKG')
            """;
        if (await command.ExecuteScalarAsync(ct) is string { Length: > 0 } errors)
        {
            throw new InvalidOperationException("PL/SQL did not compile: " + errors);
        }
    }

    private static string ReadScript(string name)
    {
        using var stream = typeof(OracleSchemaInstaller).Assembly.GetManifestResourceStream("PatientFeedback.Oracle.Schema." + name)
            ?? throw new InvalidOperationException($"Embedded script {name} is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Splits a script on lines that contain only "/" (SQL*Plus convention), dropping empty chunks.</summary>
    internal static IEnumerable<string> Split(string script) =>
        SlashLine().Split(script.Replace("\r\n", "\n", StringComparison.Ordinal))
            .Select(s => s.Trim())
            .Where(s => s.Length > 0 && !IsOnlyComments(s));

    private static bool IsOnlyComments(string chunk) =>
        chunk.Split('\n').All(l => l.TrimStart().StartsWith("--", StringComparison.Ordinal) || l.Trim().Length == 0);

    [GeneratedRegex(@"^\s*/\s*$", RegexOptions.Multiline)]
    private static partial Regex SlashLine();
}

public sealed class RetentionJobOptions
{
    /// <summary>DBMS_SCHEDULER calendar expression. KVKK caps the periodic destruction interval at six months.</summary>
    public string RepeatInterval { get; set; } = "FREQ=DAILY;BYHOUR=2;BYMINUTE=30";

    public bool Enabled { get; set; } = true;
}
