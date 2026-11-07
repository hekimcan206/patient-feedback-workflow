using System.Data;
using System.Text.RegularExpressions;
using Dapper;
using Oracle.ManagedDataAccess.Client;

namespace PatientFeedback.Oracle;

/// <summary>
/// Opens application connections. The app account owns no objects; every session is pointed at the schema owner
/// with CURRENT_SCHEMA so SQL stays unqualified and the owner name stays configuration.
/// </summary>
public sealed partial class OracleDb
{
    private readonly string _connectionString;
    private readonly string _alterSession;

    static OracleDb() => DapperSetup.Ensure();

    public OracleDb(string connectionString, string schemaOwner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _alterSession = "ALTER SESSION SET CURRENT_SCHEMA = " + OracleNames.Identifier(schemaOwner);
    }

    public async Task<OracleConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new OracleConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = _alterSession;
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}

/// <summary>Identifiers cannot be bind variables; the only ones that reach SQL text are validated here.</summary>
public static partial class OracleNames
{
    public static string Identifier(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return PlainIdentifier().IsMatch(name)
            ? name.ToUpperInvariant()
            : throw new ArgumentException($"'{name}' is not a plain Oracle identifier.", nameof(name));
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,29}$")]
    private static partial Regex PlainIdentifier();
}

internal static class OracleErrors
{
    public static bool IsUniqueViolation(OracleException ex, string constraint) =>
        ex.Number == 1 && ex.Message.Contains(constraint, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The schema stores UTC instants in TIMESTAMP columns. Dapper maps DateTimeOffset through this handler so the
/// domain keeps DateTimeOffset while Oracle sees a TIMESTAMP with full fractional seconds (not DATE).
/// </summary>
internal static class DapperSetup
{
    private static int _done;

    public static void Ensure()
    {
        if (Interlocked.Exchange(ref _done, 1) == 0)
        {
            SqlMapper.AddTypeHandler(new UtcDateTimeOffsetHandler());
        }
    }

    private sealed class UtcDateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
        {
            if (parameter is OracleParameter oracle)
            {
                oracle.OracleDbType = OracleDbType.TimeStamp;
            }
            else
            {
                parameter.DbType = DbType.DateTime2;
            }

            parameter.Value = value.UtcDateTime;
        }

        public override DateTimeOffset Parse(object value) => value switch
        {
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            DateTimeOffset dto => dto.ToUniversalTime(),
            _ => throw new DataException($"Cannot convert {value.GetType()} to DateTimeOffset."),
        };
    }
}
