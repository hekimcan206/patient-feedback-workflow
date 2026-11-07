using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using PatientFeedback.Security;
using PatientFeedback.Web.Controllers;

namespace PatientFeedback.UnitTests.Web;

/// <summary>
/// HTTP surface without a database: every case here is decided before any Oracle call (signature, size, token
/// shape, authorisation, headers). The full flow against Oracle is in the OracleTests end-to-end test.
/// </summary>
public sealed class WebSurfaceTests : IClassFixture<WebSurfaceTests.Factory>
{
    private static readonly string Secret = WebhookSecret.Generate();
    private readonly Factory _factory;

    public WebSurfaceTests(Factory factory) => _factory = factory;

    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:OracleApp", "Data Source=127.0.0.1:1/none;User Id=nobody;Password=none;Connection Timeout=1");
            builder.UseSetting("Dispatcher:Enabled", "false");
            builder.UseSetting("Webhooks:ClinicalEventSecrets:0", Secret);
            builder.UseSetting("Webhooks:ProviderSecrets:0", WebhookSecret.Generate());
            builder.UseSetting("Pii:Keks:0:Id", "test");
            builder.UseSetting("Pii:Keks:0:Key", Convert.ToBase64String(new byte[32]));
            builder.UseSetting("Pii:ActiveKekId", "test");
            builder.UseSetting("Pii:BlindIndexKey", Convert.ToBase64String(new byte[32]));
            builder.UseSetting("DemoAuth:Enabled", "true");
        }
    }

    private static HttpRequestMessage Webhook(byte[] body, string signature, long? timestamp = null) =>
        new(HttpMethod.Post, "/webhooks/clinical-events")
        {
            Content = new ByteArrayContent(body),
            Headers =
            {
                { WebhookHeaders.IdHeader, "msg_1" },
                { WebhookHeaders.TimestampHeader, (timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString(CultureInfo.InvariantCulture) },
                { WebhookHeaders.SignatureHeader, signature },
            },
        };

    [Fact]
    public async Task Unsigned_or_wrongly_signed_webhook_is_401_without_details()
    {
        using var client = _factory.CreateClient();
        var body = Encoding.UTF8.GetBytes("{}");
        var otherKey = new WebhookSigner([WebhookSecret.Parse(WebhookSecret.Generate())]);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using var response = await client.SendAsync(Webhook(body, otherKey.Sign("msg_1", now, body), now), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("Invalid webhook signature").And.NotContain("Timestamp");
    }

    [Fact]
    public async Task Stale_signed_webhook_is_401()
    {
        using var client = _factory.CreateClient();
        var body = Encoding.UTF8.GetBytes("{}");
        var old = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds();
        var signature = new WebhookSigner([WebhookSecret.Parse(Secret)]).Sign("msg_1", old, body);

        using var response = await client.SendAsync(Webhook(body, signature, old), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Correctly_signed_but_invalid_event_is_400_and_never_reaches_the_database()
    {
        using var client = _factory.CreateClient();
        var body = Encoding.UTF8.GetBytes("""{"type":"visit.completed","visitId":"V1","patientRef":"P1","patientName":"A B","phone":"0532","department":"X","completedAt":"2026-11-07T10:00:00Z","surveyConsent":true}""");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = new WebhookSigner([WebhookSecret.Parse(Secret)]).Sign("msg_1", now, body);

        using var response = await client.SendAsync(Webhook(body, signature, now), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("E.164");
    }

    [Fact]
    public async Task Oversized_webhook_is_413_before_verification()
    {
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(Webhook(new byte[64 * 1024], "v1,AAAA"), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Theory]
    [InlineData("/s/not-a-token")]
    [InlineData("/s/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA%2B")]
    public async Task Malformed_survey_link_is_410_with_no_store_and_no_referrer(string path)
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Gone);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.GetValues("Referrer-Policy").Should().Equal("no-referrer");
        response.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("script-src 'self'");
    }

    [Fact]
    public async Task Case_panel_requires_sign_in_and_json_endpoints_answer_401_instead_of_redirecting()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var page = await client.GetAsync("/cases", TestContext.Current.CancellationToken);
        using var data = await client.GetAsync("/cases/data", TestContext.Current.CancellationToken);

        page.StatusCode.Should().Be(HttpStatusCode.Redirect);
        page.Headers.Location!.ToString().Should().Contain("/account/login");
        data.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Simulation_endpoints_do_not_exist_unless_enabled()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/sim/handset", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("IPTAL", true)]
    [InlineData(" iptal ", true)]
    [InlineData("İptal", true)]
    [InlineData("stop", true)]
    [InlineData("DUR", true)]
    [InlineData("teşekkürler", false)]
    [InlineData("iptal etmeyin lütfen", false)]
    public void Stop_keywords(string text, bool expected) => WebhooksController.IsStopKeyword(text).Should().Be(expected);
}
