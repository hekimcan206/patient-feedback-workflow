using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using PatientFeedback.Cases;
using PatientFeedback.Dispatch;
using PatientFeedback.Oracle;
using PatientFeedback.Privacy;
using PatientFeedback.Scoring;
using PatientFeedback.Security;
using PatientFeedback.Web.Controllers;
using PatientFeedback.Web.Dispatch;
using PatientFeedback.Web.Hubs;
using PatientFeedback.Web.Infrastructure;
using PatientFeedback.Web.Simulation;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
builder.AddObservability();

var config = builder.Configuration;
builder.Services.Configure<WebhookOptions>(config.GetSection("Webhooks"));
builder.Services.Configure<PiiOptions>(config.GetSection("Pii"));
builder.Services.Configure<SurveyOptions>(config.GetSection("Survey"));
builder.Services.Configure<DispatcherOptions>(config.GetSection("Dispatcher"));
builder.Services.Configure<DispatchOptions>(config.GetSection("Dispatch"));
builder.Services.Configure<CaseTriggerOptions>(config.GetSection("Cases"));
builder.Services.Configure<FakeProviderOptions>(config.GetSection("FakeProviders"));
builder.Services.Configure<SimulationOptions>(config.GetSection("Simulation"));
builder.Services.Configure<DemoAuthOptions>(config.GetSection("DemoAuth"));

builder.Services.AddSingleton(TimeProvider.System);

// Domain services.
builder.Services.AddSingleton<ScoringService>();
builder.Services.AddSingleton(sp => new CaseTriggerPolicy(sp.GetRequiredService<IOptions<CaseTriggerOptions>>().Value));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<DispatchOptions>>().Value);
builder.Services.AddSingleton(sp => new RetryPolicy(sp.GetRequiredService<DispatchOptions>()));
builder.Services.AddSingleton<DispatchPlanner>();
builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<IOptions<WebhookOptions>>().Value;
    var clock = sp.GetRequiredService<TimeProvider>();
    var tolerance = TimeSpan.FromSeconds(o.ToleranceSeconds);
    return new WebhookVerifiers(
        new WebhookVerifier(o.ClinicalEventSecrets.Select(WebhookSecret.Parse), clock, tolerance),
        new WebhookVerifier(o.ProviderSecrets.Select(WebhookSecret.Parse), clock, tolerance));
});
builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<IOptions<PiiOptions>>().Value;
    return new PiiProtector(o.Keks.Select(k => KeyEncryptionKey.FromBase64(k.Id, k.Key)), o.ActiveKekId);
});
builder.Services.AddSingleton(sp => new BlindIndex(Convert.FromBase64String(sp.GetRequiredService<IOptions<PiiOptions>>().Value.BlindIndexKey)));

// Oracle (Dapper repositories, app account).
var oracleApp = config.GetConnectionString("OracleApp");
var schemaOwner = config["Oracle:SchemaOwner"] ?? "PF_OWNER";
var health = builder.Services.AddHealthChecks();
if (!string.IsNullOrWhiteSpace(oracleApp))
{
    builder.Services.AddSingleton(new OracleDb(oracleApp, schemaOwner));
    health.AddCheck("oracle", new OracleHealthCheck(oracleApp), tags: [HealthEndpoints.ReadyTag]);
}

builder.Services.AddSingleton<IntakeRepository>();
builder.Services.AddSingleton<OutboxRepository>();
builder.Services.AddSingleton<SurveyRepository>();
builder.Services.AddSingleton<CaseRepository>();
builder.Services.AddSingleton<ContactRepository>();
builder.Services.AddSingleton<AuditRepository>();
builder.Services.AddSingleton<StatsRepository>();

// Messaging: fake providers stand in for the WhatsApp Business and SMS gateway HTTP clients.
builder.Services.AddSingleton<FakeHandset>();
builder.Services.AddSingleton<IChannelAdapter, FakeWhatsAppProvider>();
builder.Services.AddSingleton<IChannelAdapter, FakeSmsProvider>();
builder.Services.AddSingleton<DispatchSignal>();
builder.Services.AddSingleton<SurveyDispatcher>();
if (config.GetValue("Dispatcher:Enabled", true))
{
    builder.Services.AddHostedService(sp => sp.GetRequiredService<SurveyDispatcher>());
    builder.Services.AddHostedService<SlaWatcher>();
}

// Presentation: MVC + SignalR, cookie sign-in with role policies.
builder.Services.AddControllersWithViews()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSignalR();
builder.Services.AddSingleton<CaseNotifier>();
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions.TryAdd("traceId", Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/account/login";
        options.AccessDeniedPath = "/account/denied";
        options.Cookie.Name = "pf.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        // JSON endpoints answer 401/403 instead of redirecting to the login page.
        options.Events.OnRedirectToLogin = context => RedirectOrStatus(context, StatusCodes.Status401Unauthorized);
        options.Events.OnRedirectToAccessDenied = context => RedirectOrStatus(context, StatusCodes.Status403Forbidden);
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.CaseHandling, p => p.RequireRole(Roles.Agent, Roles.CaseManager))
    .AddPolicy(Policies.PiiReveal, p => p.RequireRole(Roles.CaseManager))
    .AddPolicy(Policies.AuditRead, p => p.RequireRole(Roles.PrivacyOfficer));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Tokens are unguessable (256 bits); the limit stops scripted abuse of the public survey pages.
    options.AddPolicy(RateLimits.Survey, context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = config.GetValue("RateLimits:SurveyPerMinute", 60), Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

// One image, two roles: `--init-oracle` installs schema, PL/SQL, grants and the DBMS_SCHEDULER retention job with the
// schema-owner account, then exits; the long-running app connects with the least-privilege application account.
if (args.Contains("--init-oracle"))
{
    var owner = app.Configuration.GetConnectionString("OracleOwner");
    if (string.IsNullOrWhiteSpace(owner))
    {
        app.Logger.LogError("ConnectionStrings:OracleOwner is required for --init-oracle");
        return 1;
    }

    await new OracleSchemaInstaller(owner).InstallAsync(
        app.Configuration["Oracle:AppUser"] ?? "PF_APP",
        app.Configuration.GetSection("Retention").Get<RetentionJobOptions>() ?? new RetentionJobOptions());
    app.Logger.LogInformation("Oracle schema, PL/SQL packages, grants and retention job installed");
    return 0;
}

app.UseExceptionHandler("/error");
app.UseStatusCodePages();
app.UseSecurityHeaders();
app.UseSerilogRequestLogging(options =>
{
    // Survey URLs carry the link token: log the route template, never the raw path.
    options.MessageTemplate = "HTTP {RequestMethod} {RouteTemplate} responded {StatusCode} in {Elapsed:0.0} ms";
    options.EnrichDiagnosticContext = (diagnostics, context) =>
        diagnostics.Set("RouteTemplate", (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched");
    options.GetLevel = (context, _, exception) =>
        exception is null && context.Response.StatusCode < 400 && context.Request.Path.StartsWithSegments("/health")
            ? Serilog.Events.LogEventLevel.Verbose
            : Serilog.Events.LogEventLevel.Information;
});
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthEndpoints();
app.MapControllers();
app.MapHub<CaseHub>(CaseHub.Path);
app.MapGet("/", () => Results.Redirect("/cases")).ExcludeFromDescription();
app.Map("/error", () => Results.Problem(title: "Unexpected error")).ExcludeFromDescription();
if (app.Configuration.GetValue<bool>("Simulation:Enabled"))
{
    app.MapSimulationEndpoints();
}

await app.RunAsync();
return 0;

static Task RedirectOrStatus(Microsoft.AspNetCore.Authentication.RedirectContext<CookieAuthenticationOptions> context, int status)
{
    if (context.Request.Path.StartsWithSegments("/cases/data") || context.Request.Path.StartsWithSegments("/hubs")
        || context.Request.Headers.Accept.ToString().Contains("application/json", StringComparison.Ordinal)
        || HttpMethods.IsPost(context.Request.Method) && !context.Request.Path.StartsWithSegments("/account"))
    {
        context.Response.StatusCode = status;
        return Task.CompletedTask;
    }

    context.Response.Redirect(context.RedirectUri);
    return Task.CompletedTask;
}

/// <summary>Entry point marker for WebApplicationFactory in tests.</summary>
public partial class Program;

internal static class RateLimits
{
    public const string Survey = "survey";
}
