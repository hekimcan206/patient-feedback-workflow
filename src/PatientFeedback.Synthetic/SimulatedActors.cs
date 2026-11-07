using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PatientFeedback.Domain;
using PatientFeedback.Scoring;
using PatientFeedback.Security;

namespace PatientFeedback.Synthetic;

/// <summary>The hospital information system side: sends signed <c>visit.completed</c> webhooks.</summary>
public sealed class SimulatedHis(HttpClient http, WebhookSigner signer, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static byte[] Serialize(VisitCompletedEvent visit) => JsonSerializer.SerializeToUtf8Bytes(visit, Json);

    public async Task<HttpResponseMessage> SendAsync(VisitCompletedEvent visit, string? messageId = null, CancellationToken ct = default)
    {
        var body = Serialize(visit);
        return await SendRawAsync(body, messageId ?? "msg_" + Guid.NewGuid().ToString("N"), clock.GetUtcNow().ToUnixTimeSeconds(), ct);
    }

    public async Task<HttpResponseMessage> SendRawAsync(byte[] body, string messageId, long timestamp, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/clinical-events")
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add(WebhookHeaders.IdHeader, messageId);
        request.Headers.Add(WebhookHeaders.TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(WebhookHeaders.SignatureHeader, signer.Sign(messageId, timestamp, body));
        return await http.SendAsync(request, ct);
    }
}

public sealed record HandsetMessageDto(long Sequence, string Channel, string Phone, string Text, string Link, DateTimeOffset DeliveredAt);

/// <summary>The patient side: reads the fake handset inbox and answers surveys like a phone browser would.</summary>
public sealed partial class SimulatedPatient(HttpClient http)
{
    public async Task<IReadOnlyList<HandsetMessageDto>> InboxAsync(long after, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<HandsetMessageDto>>($"/sim/handset?after={after}&max=2000", ct) ?? [];

    /// <summary>Opens the link (GET), then submits the form with its antiforgery token (POST). Returns the POST status.</summary>
    public async Task<HttpStatusCode> AnswerAsync(string link, SurveyAnswers answers, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(answers);
        var path = new Uri(link).AbsolutePath;
        using var page = await http.GetAsync(path, ct);
        if (!page.IsSuccessStatusCode)
        {
            return page.StatusCode;
        }

        var html = await page.Content.ReadAsStringAsync(ct);
        var token = AntiforgeryField().Match(html);
        if (!token.Success)
        {
            throw new InvalidOperationException("Survey page has no antiforgery token.");
        }

        var form = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", WebUtility.HtmlDecode(token.Groups[1].Value)),
            new("Nps", answers.Nps.ToString(CultureInfo.InvariantCulture)),
        };
        if (answers.Csat is { } csat)
        {
            form.Add(new("Csat", csat.ToString(CultureInfo.InvariantCulture)));
        }

        if (answers.Ces is { } ces)
        {
            form.Add(new("Ces", ces.ToString(CultureInfo.InvariantCulture)));
        }

        if (answers.Comment is { } comment)
        {
            form.Add(new("Comment", comment));
        }

        using var content = new FormUrlEncodedContent(form);
        using var response = await http.PostAsync(path, content, ct);
        return response.StatusCode;
    }

    public static string TokenOf(string link) => new Uri(link).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1];

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryField();
}
