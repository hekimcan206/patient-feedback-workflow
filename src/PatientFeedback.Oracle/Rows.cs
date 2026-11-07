using System.Data;
using System.Globalization;
using Dapper;
using Oracle.ManagedDataAccess.Types;
using PatientFeedback.Dispatch;

namespace PatientFeedback.Oracle;

internal static class Db
{
    /// <summary>Adds an output bind for <c>RETURNING … INTO :name</c>.</summary>
    public static DynamicParameters WithOutputId(this DynamicParameters parameters, string name = "id")
    {
        parameters.Add(name, dbType: DbType.Int64, direction: ParameterDirection.Output);
        return parameters;
    }

    /// <summary>ODP.NET hands output binds back as provider types (OracleDecimal); Dapper's Get&lt;long&gt; cannot cast those.</summary>
    public static long ReadId(this DynamicParameters parameters, string name = "id") =>
        parameters.Get<object>(name) switch
        {
            OracleDecimal d => d.ToInt64(),
            null or DBNull => throw new DataException($"Output parameter {name} was not set."),
            var other => Convert.ToInt64(other, CultureInfo.InvariantCulture),
        };

    public static string ToDb(this Channel channel) => channel switch
    {
        Channel.WhatsApp => "WHATSAPP",
        Channel.Sms => "SMS",
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, null),
    };

    public static Channel ToChannel(string value) => value switch
    {
        "WHATSAPP" => Channel.WhatsApp,
        "SMS" => Channel.Sms,
        _ => throw new DataException($"Unknown channel '{value}'."),
    };
}
