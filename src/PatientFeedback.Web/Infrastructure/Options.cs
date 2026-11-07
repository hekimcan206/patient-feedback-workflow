namespace PatientFeedback.Web.Infrastructure;

/// <summary>Signing secrets for inbound webhooks. Several may be active during rotation.</summary>
public sealed class WebhookOptions
{
    /// <summary>Secrets accepted on <c>/webhooks/clinical-events</c> (from the hospital information system).</summary>
    public List<string> ClinicalEventSecrets { get; set; } = [];

    /// <summary>Secrets accepted on <c>/webhooks/channels/*/inbound</c> (from the messaging providers).</summary>
    public List<string> ProviderSecrets { get; set; } = [];

    public int ToleranceSeconds { get; set; } = 300;

    /// <summary>A visit event is a few hundred bytes; anything far larger is a mistake or abuse.</summary>
    public int MaxBodyBytes { get; set; } = 16 * 1024;
}

public sealed class PiiOptions
{
    public List<KekOption> Keks { get; set; } = [];

    public string ActiveKekId { get; set; } = "";

    /// <summary>Base64 HMAC key for blind indexes. Separate from the KEKs so it can be rotated on its own schedule.</summary>
    public string BlindIndexKey { get; set; } = "";
}

public sealed class KekOption
{
    public string Id { get; set; } = "";

    public string Key { get; set; } = "";
}

public sealed class SurveyOptions
{
    /// <summary>Public base URL used in survey links, e.g. https://anket.example-hastane.test.</summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:18440";
}

public sealed class DispatcherOptions
{
    public bool Enabled { get; set; } = true;

    public int BatchSize { get; set; } = 32;

    public int Concurrency { get; set; } = 8;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan Lease { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan ProviderTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan SlaCheckInterval { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>Fake provider behaviour. Probabilities are per send attempt.</summary>
public sealed class FakeProviderOptions
{
    public double WhatsAppRateLimitRate { get; set; } = 0.03;

    public double WhatsAppTimeoutRate { get; set; } = 0.01;

    public double SmsServerErrorRate { get; set; } = 0.02;

    public int MinLatencyMs { get; set; } = 15;

    public int MaxLatencyMs { get; set; } = 60;

    public int? Seed { get; set; }
}

public sealed class SimulationOptions
{
    /// <summary>Exposes <c>/sim/*</c> (the fake handset inbox and the latency read-out). Never enable in production.</summary>
    public bool Enabled { get; set; }
}

public sealed class DemoAuthOptions
{
    /// <summary>One-click sign-in as a demo user. Stands in for the hospital's OIDC provider; never enable in production.</summary>
    public bool Enabled { get; set; } = true;
}
