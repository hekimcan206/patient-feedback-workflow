using System.Globalization;
using PatientFeedback.Security;
using PatientFeedback.Synthetic;

// Visit simulator for the docker compose demo. Acts as both ends the app talks to:
//   * the hospital information system: posts signed visit.completed webhooks at a steady rate;
//   * the patients: reads survey links from the fake providers' handset inbox and answers most of them.
// All data is synthetic (Bogus, tr locale); see VisitGenerator.

var target = Env("TARGET_URL", "http://localhost:18440");
var secret = Env("WEBHOOK_SECRET", "");
var visitsPerMinute = double.Parse(Env("VISITS_PER_MINUTE", "30"), CultureInfo.InvariantCulture);
var answerRate = double.Parse(Env("ANSWER_RATE", "0.7"), CultureInfo.InvariantCulture);
// A fresh seed per start: re-running with a fixed seed would replay the same visit ids (correctly ignored as duplicates).
var seed = int.Parse(Env("SEED", Random.Shared.Next(1, int.MaxValue).ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
if (string.IsNullOrWhiteSpace(secret))
{
    Console.Error.WriteLine("WEBHOOK_SECRET is required.");
    return 2;
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Cancel();

using var http = new HttpClient { BaseAddress = new Uri(target), Timeout = TimeSpan.FromSeconds(15) };
await WaitForAppAsync(http, stop.Token);

var his = new SimulatedHis(http, new WebhookSigner([WebhookSecret.Parse(secret)]), TimeProvider.System);
var patient = new SimulatedPatient(http);
// Two generators: the loops run concurrently and Bogus randomizers are not thread-safe.
var visitGenerator = new VisitGenerator(seed);
var answerGenerator = new VisitGenerator(seed + 1);
var answerRandom = new Random(seed);

var visits = Task.Run(async () =>
{
    var interval = TimeSpan.FromMinutes(1 / visitsPerMinute);
    var sent = 0;
    while (!stop.IsCancellationRequested)
    {
        try
        {
            var visit = visitGenerator.Visit(DateTimeOffset.UtcNow.AddMinutes(-5));
            using var response = await his.SendAsync(visit, ct: stop.Token);
            sent++;
            if (sent % 10 == 0 || !response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[his] {sent} visits sent (last: {(int)response.StatusCode})");
            }
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"[his] send failed: {ex.Message}");
        }

        await Task.Delay(interval, stop.Token).ContinueWith(_ => { }, TaskScheduler.Default);
    }
});

var answers = Task.Run(async () =>
{
    long after = 0;
    var answered = 0;
    while (!stop.IsCancellationRequested)
    {
        try
        {
            foreach (var message in await patient.InboxAsync(after, stop.Token))
            {
                after = Math.Max(after, message.Sequence);
                if (answerRandom.NextDouble() >= answerRate)
                {
                    continue;
                }

                var department = DepartmentOf(message.Text);
                var status = await patient.AnswerAsync(message.Link, answerGenerator.Answers(department), stop.Token);
                answered++;
                if (answered % 10 == 0 || (int)status >= 400)
                {
                    Console.WriteLine($"[patients] {answered} surveys answered (last: {(int)status})");
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException)
        {
            Console.WriteLine($"[patients] {ex.Message}");
        }

        await Task.Delay(TimeSpan.FromSeconds(2), stop.Token).ContinueWith(_ => { }, TaskScheduler.Default);
    }
});

await Task.WhenAll(visits, answers);
return 0;

static string DepartmentOf(string text) =>
    VisitGenerator.Departments.FirstOrDefault(d => text.Contains(d, StringComparison.Ordinal)) ?? "";

static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

static async Task WaitForAppAsync(HttpClient http, CancellationToken ct)
{
    for (var i = 0; i < 120 && !ct.IsCancellationRequested; i++)
    {
        try
        {
            using var response = await http.GetAsync("/health/ready", ct);
            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine("[sim] app is ready");
                return;
            }
        }
        catch (HttpRequestException)
        {
        }

        await Task.Delay(TimeSpan.FromSeconds(2), ct);
    }
}
