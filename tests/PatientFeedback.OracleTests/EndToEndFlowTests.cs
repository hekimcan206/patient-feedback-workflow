using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using PatientFeedback.Dispatch;
using PatientFeedback.Scoring;
using PatientFeedback.Security;
using PatientFeedback.Synthetic;
using PatientFeedback.Web.Hubs;

namespace PatientFeedback.OracleTests;

/// <summary>
/// The whole flow through the real app and a real Oracle: signed webhook → HMAC verified → survey + outbox →
/// dispatcher → fake WhatsApp/SMS → tokenised survey page → low score → case with SLA → SignalR push to the panel.
/// </summary>
public sealed partial class EndToEndFlowTests(OracleFixture fx) : IAsyncLifetime
{
    private static readonly string Secret = WebhookSecret.Generate();
    private AppFactory _app = null!;

    public async ValueTask InitializeAsync()
    {
        await fx.ResetAsync();
        _app = new AppFactory(fx);
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private sealed class AppFactory(OracleFixture fx) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:OracleApp", fx.AppConnectionString);
            builder.UseSetting("Oracle:SchemaOwner", "PF_OWNER");
            builder.UseSetting("Webhooks:ClinicalEventSecrets:0", Secret);
            builder.UseSetting("Webhooks:ProviderSecrets:0", WebhookSecret.Generate());
            builder.UseSetting("Pii:Keks:0:Id", "kek-test-1");
            builder.UseSetting("Pii:Keks:0:Key", Convert.ToBase64String(OracleFixture.KekBytes));
            builder.UseSetting("Pii:ActiveKekId", "kek-test-1");
            builder.UseSetting("Pii:BlindIndexKey", Convert.ToBase64String(new byte[32]));
            builder.UseSetting("Survey:PublicBaseUrl", "http://localhost");
            builder.UseSetting("Dispatch:SendDelay", "00:00:00");
            builder.UseSetting("Dispatch:QuietHoursEnabled", "false");
            builder.UseSetting("Dispatch:RetryBaseDelay", "00:00:00.010");
            builder.UseSetting("Dispatcher:PollInterval", "00:00:00.200");
            builder.UseSetting("FakeProviders:WhatsAppRateLimitRate", "0");
            builder.UseSetting("FakeProviders:WhatsAppTimeoutRate", "0");
            builder.UseSetting("FakeProviders:SmsServerErrorRate", "0");
            builder.UseSetting("Simulation:Enabled", "true");
            builder.UseSetting("DemoAuth:Enabled", "true");
        }
    }

    [Fact]
    public async Task Visit_event_to_survey_to_low_score_opens_a_case_and_the_panel_hears_about_it()
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = _app.CreateClient();
        var his = new SimulatedHis(http, new WebhookSigner([WebhookSecret.Parse(Secret)]), TimeProvider.System);
        var patient = new SimulatedPatient(http);

        // A signed-in case manager has the panel open.
        using var staff = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        var cookie = await SignInAsync(staff, "mehmet.manager", ct);
        var pushed = new TaskCompletionSource<CaseDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(_app.Server.BaseAddress, CaseHub.Path), o =>
            {
                o.HttpMessageHandlerFactory = _ => _app.Server.CreateHandler();
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                o.Headers["Cookie"] = cookie;
            })
            .Build();
        hub.On<CaseDto>(CaseNotifier.CaseUpserted, c => pushed.TrySetResult(c));
        await hub.StartAsync(ct);

        // 1. The HIS reports a completed visit (signed webhook).
        var visit = new VisitGenerator(42).Visit(DateTimeOffset.UtcNow.AddMinutes(-10), consentRate: 1) with
        {
            Department = "Emergency",
            Phone = "+905320004100",
            PreferredChannel = Channel.WhatsApp,
        };
        using (var accepted = await his.SendAsync(visit, "msg_e2e_1", ct))
        {
            accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
            (await accepted.Content.ReadAsStringAsync(ct)).Should().Contain("SurveyScheduled");
        }

        // 2. A replay of the same delivery is acknowledged and ignored.
        using (var replay = await his.SendAsync(visit, "msg_e2e_1", ct))
        {
            (await replay.Content.ReadAsStringAsync(ct)).Should().Contain("DuplicateDelivery");
        }

        // 3. The dispatcher sends it; the patient's phone receives the link.
        var message = await EventuallyAsync(async () => (await patient.InboxAsync(0, ct)).FirstOrDefault(m => m.Phone == visit.Phone), ct);
        message.Channel.Should().Be("WhatsApp");
        message.Text.Should().Contain("Emergency");

        // 4. Opening the link twice (link preview + patient) does not consume it; a low score is submitted.
        (await http.GetAsync(new Uri(message.Link).AbsolutePath, ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        var status = await patient.AnswerAsync(message.Link, new SurveyAnswers(2, 1, 3, "Acil serviste dört saat bekledim."), ct);
        status.Should().Be(HttpStatusCode.OK);

        // 5. The case is opened with a 4-hour SLA and pushed to the panel over SignalR.
        var live = await pushed.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        live.Severity.Should().Be("High");
        live.Department.Should().Be("Emergency");
        live.Nps.Should().Be(2);
        (live.SlaDueAt - live.OpenedAt).Should().Be(TimeSpan.FromHours(4));

        // 6. The link is single-use.
        (await patient.AnswerAsync(message.Link, new SurveyAnswers(10, 5, 7, null), ct)).Should().Be(HttpStatusCode.Gone);

        // 7. The case manager reads the comment and reveals the contact (both audited).
        using var detail = await staff.SendAsync(Json(HttpMethod.Get, $"/cases/{live.CaseId}", cookie), ct);
        (await detail.Content.ReadAsStringAsync(ct)).Should().Contain("Acil serviste dört saat bekledim.");
        (await fx.OwnerScalarAsync<string>("SELECT purpose FROM audit_log WHERE action = 'PII_READ' AND actor = 'mehmet.manager'")).Should().Be("case-handling");
        (await fx.OwnerScalarAsync<int>("SELECT COUNT(*) FROM audit_log WHERE action = 'PII_READ' AND actor = 'dispatcher' AND purpose = 'survey-dispatch'")).Should().Be(1);
    }

    [Fact]
    public async Task Number_not_on_whatsapp_falls_back_to_sms()
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = _app.CreateClient();
        var his = new SimulatedHis(http, new WebhookSigner([WebhookSecret.Parse(Secret)]), TimeProvider.System);
        var visit = new VisitGenerator(7).Visit(DateTimeOffset.UtcNow, consentRate: 1) with { Phone = "+905320004113", PreferredChannel = Channel.WhatsApp };

        (await his.SendAsync(visit, "msg_e2e_fallback", ct)).StatusCode.Should().Be(HttpStatusCode.Accepted);

        var message = await EventuallyAsync(async () => (await new SimulatedPatient(http).InboxAsync(0, ct)).FirstOrDefault(m => m.Phone == visit.Phone), ct);
        message.Channel.Should().Be("Sms");
        (await fx.OwnerScalarAsync<string>("SELECT channel || '|' || status || '|' || total_attempts FROM outbox_message")).Should().Be("SMS|SENT|2");
    }

    [Fact]
    public async Task Agent_without_case_manager_role_cannot_reveal_contacts()
    {
        var ct = TestContext.Current.CancellationToken;
        using var staff = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        var cookie = await SignInAsync(staff, "ayse.agent", ct);
        using var page = await staff.SendAsync(Json(HttpMethod.Get, "/cases", cookie), ct);
        var token = AntiforgeryField().Match(await page.Content.ReadAsStringAsync(ct)).Groups[1].Value;

        using var reveal = Json(HttpMethod.Post, "/cases/1/reveal", cookie);
        reveal.Headers.Add("RequestVerificationToken", WebUtility.HtmlDecode(token));
        reveal.Content = new FormUrlEncodedContent([new("Purpose", "call-back-patient")]);
        using var response = await staff.SendAsync(reveal, ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static HttpRequestMessage Json(HttpMethod method, string path, string cookie)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("Accept", "application/json");
        return request;
    }

    /// <summary>Demo sign-in through the real login form; returns the cookies to replay on later requests.</summary>
    private static async Task<string> SignInAsync(HttpClient client, string user, CancellationToken ct)
    {
        using var login = await client.GetAsync("/account/login", ct);
        var html = await login.Content.ReadAsStringAsync(ct);
        var antiforgeryCookie = string.Join("; ", login.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
        using var post = new HttpRequestMessage(HttpMethod.Post, "/account/login")
        {
            Content = new FormUrlEncodedContent(
            [
                new("__RequestVerificationToken", WebUtility.HtmlDecode(AntiforgeryField().Match(html).Groups[1].Value)),
                new("userName", user),
            ]),
        };
        post.Headers.Add("Cookie", antiforgeryCookie);
        using var response = await client.SendAsync(post, ct);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var auth = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("pf.auth=", StringComparison.Ordinal)).Split(';')[0];
        return antiforgeryCookie + "; " + auth;
    }

    private static async Task<T> EventuallyAsync<T>(Func<Task<T?>> probe, CancellationToken ct)
        where T : class
    {
        for (var i = 0; i < 100; i++)
        {
            if (await probe() is { } value)
            {
                return value;
            }

            await Task.Delay(100, ct);
        }

        throw new TimeoutException("Condition not met within 10 s.");
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryField();
}
