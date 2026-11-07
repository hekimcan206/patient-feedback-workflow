using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace PatientFeedback.Web.Infrastructure;

/// <summary>Business metrics exported through OpenTelemetry (meter <c>PatientFeedback</c>).</summary>
public sealed class FeedbackMetrics : IDisposable
{
    public const string MeterName = "PatientFeedback";

    public static readonly ActivitySource Activities = new(ObservabilityExtensions.ActivitySourceName);

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _webhooks;
    private readonly Counter<long> _sendAttempts;
    private readonly Histogram<double> _providerLatency;
    private readonly Counter<long> _responses;
    private readonly Counter<long> _casesOpened;

    public FeedbackMetrics()
    {
        _webhooks = _meter.CreateCounter<long>("pf.webhooks", description: "Inbound clinical-event webhooks by verdict/outcome");
        _sendAttempts = _meter.CreateCounter<long>("pf.dispatch.attempts", description: "Provider send attempts by channel and result");
        _providerLatency = _meter.CreateHistogram<double>("pf.dispatch.provider_latency", unit: "ms");
        _responses = _meter.CreateCounter<long>("pf.survey.responses");
        _casesOpened = _meter.CreateCounter<long>("pf.cases.opened");
    }

    public void Webhook(string result) => _webhooks.Add(1, new KeyValuePair<string, object?>("result", result));

    public void SendAttempt(string channel, string status, double latencyMs)
    {
        _sendAttempts.Add(1, new KeyValuePair<string, object?>("channel", channel), new KeyValuePair<string, object?>("status", status));
        _providerLatency.Record(latencyMs, new KeyValuePair<string, object?>("channel", channel));
    }

    public void Response(string npsCategory) => _responses.Add(1, new KeyValuePair<string, object?>("nps_category", npsCategory));

    public void CaseOpened(string severity) => _casesOpened.Add(1, new KeyValuePair<string, object?>("severity", severity));

    public void Dispose() => _meter.Dispose();
}
