using System.Security.Cryptography;
using Dapper;
using DotNet.Testcontainers.Configurations;
using Oracle.ManagedDataAccess.Client;
using PatientFeedback.Dispatch;
using PatientFeedback.Oracle;
using PatientFeedback.Privacy;
using Testcontainers.Oracle;

[assembly: AssemblyFixture(typeof(PatientFeedback.OracleTests.OracleFixture))]

namespace PatientFeedback.OracleTests;

/// <summary>
/// One Oracle Database 23ai Free container per test run. Users come from the same init script docker compose uses;
/// the schema, packages, grants and retention job are installed as PF_OWNER; repositories run as PF_APP.
/// </summary>
public sealed class OracleFixture : IAsyncLifetime
{
    // Test-only credentials for a throwaway container.
    private const string OwnerPassword = "OwnerTest1";
    private const string AppPassword = "AppTest1";

    private readonly OracleContainer _container = new OracleBuilder("gvenzl/oracle-free:23-slim-faststart")
        .WithEnvironment("OWNER_PASSWORD", OwnerPassword)
        .WithEnvironment("APP_PASSWORD", AppPassword)
        .WithResourceMapping(
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "oracle-init", "01-users.sh")),
            "/container-entrypoint-initdb.d/01-users.sh",
            fileMode: UnixFileModes.UserRead | UnixFileModes.UserWrite | UnixFileModes.UserExecute | UnixFileModes.GroupRead | UnixFileModes.OtherRead)
        .WithLabel("project", "patient-feedback-workflow-tests")
        .Build();

    public string OwnerConnectionString => WithCredentials("pf_owner", OwnerPassword);

    public string AppConnectionString => WithCredentials("pf_app", AppPassword);

    public OracleDb AppDb => new(AppConnectionString, "pf_owner");

    public static byte[] KekBytes { get; } = RandomNumberGenerator.GetBytes(32);

    public PiiProtector Protector { get; } = new([new KeyEncryptionKey("kek-test-1", KekBytes)], "kek-test-1");

    public BlindIndex BlindIndex { get; } = new(RandomNumberGenerator.GetBytes(32));

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        // The job is installed disabled: tests run it explicitly so nothing fires during a test.
        await new OracleSchemaInstaller(OwnerConnectionString).InstallAsync("pf_app", new RetentionJobOptions { Enabled = false });
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Deletes all business rows (as the owner: the app account has no DELETE).</summary>
    public async Task ResetAsync()
    {
        await using var connection = new OracleConnection(OwnerConnectionString);
        await connection.OpenAsync();
        foreach (var table in new[] { "case_event", "feedback_case", "survey_response", "outbox_message", "survey", "clinical_event", "contact", "audit_log", "retention_run" })
        {
            await connection.ExecuteAsync("DELETE FROM " + table);
        }
    }

    public async Task<T> OwnerScalarAsync<T>(string sql, object? param = null)
    {
        await using var connection = new OracleConnection(OwnerConnectionString);
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<T>(sql, param) ?? throw new InvalidOperationException("No value: " + sql);
    }

    public async Task OwnerExecuteAsync(string sql, object? param = null)
    {
        await using var connection = new OracleConnection(OwnerConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(sql, param);
    }

    public static DispatchOptions ImmediateDispatch() => new()
    {
        SendDelay = TimeSpan.Zero,
        QuietHoursEnabled = false,
        RetryBaseDelay = TimeSpan.FromMilliseconds(1),
        RetryMaxDelay = TimeSpan.FromMilliseconds(5),
    };

    private string WithCredentials(string user, string password) =>
        new OracleConnectionStringBuilder(_container.GetConnectionString())
        {
            UserID = user,
            Password = password,
        }.ConnectionString;
}
