using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;

namespace PatientFeedback.Web.Infrastructure;

internal static class ObservabilityExtensions
{
    public const string ActivitySourceName = "PatientFeedback";

    /// <summary>
    /// Structured JSON logs on stdout plus OpenTelemetry traces and metrics. The OTLP exporter is enabled only when
    /// OTEL_EXPORTER_OTLP_ENDPOINT is set, so the service runs without a collector.
    /// </summary>
    public static WebApplicationBuilder AddObservability(this WebApplicationBuilder builder)
    {
        builder.Services.AddSerilog((services, logger) => logger
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .WriteTo.Console(new RenderedCompactJsonFormatter()));

        var serviceName = builder.Configuration["OTEL_SERVICE_NAME"] ?? builder.Environment.ApplicationName;
        var serviceVersion = typeof(ObservabilityExtensions).Assembly.GetName().Version?.ToString(3);

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName, serviceVersion: serviceVersion))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(options =>
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
                .AddSource(ActivitySourceName))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(FeedbackMetrics.MeterName));

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            otel.UseOtlpExporter();
        }

        builder.Services.AddSingleton<FeedbackMetrics>();
        return builder;
    }
}
