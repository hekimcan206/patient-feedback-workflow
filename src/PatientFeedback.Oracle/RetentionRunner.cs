using System.Data;
using Dapper;
using Oracle.ManagedDataAccess.Client;

namespace PatientFeedback.Oracle;

/// <summary>
/// Runs <c>retention_pkg.run</c> on demand (tests, operators). Needs the schema-owner account: the application account
/// has no EXECUTE on the retention package, so a compromised web tier cannot trigger mass anonymisation.
/// </summary>
public sealed class RetentionRunner(string ownerConnectionString)
{
    public async Task<long> RunAsync(DateTimeOffset? asOf, string triggeredBy, int batchSize = 500, CancellationToken ct = default)
    {
        await using var connection = new OracleConnection(ownerConnectionString);
        await connection.OpenAsync(ct);
        var p = new DynamicParameters(new { asOf, triggeredBy, batchSize }).WithOutputId("runId");
        await connection.ExecuteAsync(new CommandDefinition(
            "BEGIN retention_pkg.run(p_as_of => :asOf, p_triggered_by => :triggeredBy, p_batch_size => :batchSize, p_run_id => :runId); END;",
            p, cancellationToken: ct));
        return p.ReadId("runId");
    }

    /// <summary>Runs the scheduler job synchronously in this session (what the nightly window would do).</summary>
    public async Task RunJobNowAsync(CancellationToken ct = default)
    {
        await using var connection = new OracleConnection(ownerConnectionString);
        await connection.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "BEGIN DBMS_SCHEDULER.RUN_JOB(job_name => retention_pkg.c_job_name, use_current_session => TRUE); END;",
            commandType: CommandType.Text, cancellationToken: ct));
    }
}
