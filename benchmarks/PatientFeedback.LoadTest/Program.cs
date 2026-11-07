using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using PatientFeedback.Security;
using PatientFeedback.Synthetic;

// Load test against a running stack (docker compose up, simulator stopped). Two measurements:
//   1. Intake + dispatch: N signed visit webhooks at concurrency C. Client-side webhook latency, and
//      webhook-received → provider-accepted latency computed in Oracle (PERCENTILE_CONT over outbox timestamps).
//   2. Survey throughput: every delivered link is opened (GET) and answered (POST) at concurrency C.
// Usage: dotnet run -c Release --project benchmarks/PatientFeedback.LoadTest -- <baseUrl> <whsec_...> [visits] [concurrency]

var baseUrl = args.ElementAtOrDefault(0) ?? "http://localhost:18440";
var secret = args.ElementAtOrDefault(1) ?? throw new ArgumentException("webhook secret required");
var visits = int.Parse(args.ElementAtOrDefault(2) ?? "2000", CultureInfo.InvariantCulture);
var concurrency = int.Parse(args.ElementAtOrDefault(3) ?? "16", CultureInfo.InvariantCulture);

using var handler = new SocketsHttpHandler { MaxConnectionsPerServer = concurrency * 2, UseCookies = false };
using var http = new HttpClient(handler) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
var his = new SimulatedHis(http, new WebhookSigner([WebhookSecret.Parse(secret)]), TimeProvider.System);
var generator = new VisitGenerator(Environment.TickCount);

Console.WriteLine($"# patient-feedback load test — {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC");
Console.WriteLine($"target {baseUrl}, visits {visits}, concurrency {concurrency}, client {RuntimeInformation.OSDescription}, {Environment.ProcessorCount} logical CPUs");

// Warm-up: connection pools, JIT, Oracle session pool. Not measured.
var warm = Enumerable.Range(0, 50).Select(_ => generator.Visit(DateTimeOffset.UtcNow, consentRate: 1)).ToList();
await Parallel.ForEachAsync(warm, new ParallelOptions { MaxDegreeOfParallelism = concurrency }, async (v, ct) => (await his.SendAsync(v, ct: ct)).Dispose());
var inboxStart = await LatestSequenceAsync();
await Task.Delay(3000);

// ---- 1. intake + dispatch ------------------------------------------------------------------------------
var batch = Enumerable.Range(0, visits).Select(_ => generator.Visit(DateTimeOffset.UtcNow, consentRate: 1)).ToList();
var webhookMs = new ConcurrentBag<double>();
var statuses = new ConcurrentDictionary<HttpStatusCode, int>();
var since = DateTimeOffset.UtcNow.AddSeconds(-1);
inboxStart = await LatestSequenceAsync();
var wall = Stopwatch.StartNew();
await Parallel.ForEachAsync(batch, new ParallelOptions { MaxDegreeOfParallelism = concurrency }, async (visit, ct) =>
{
    var started = Stopwatch.GetTimestamp();
    using var response = await his.SendAsync(visit, ct: ct);
    webhookMs.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    statuses.AddOrUpdate(response.StatusCode, 1, (_, n) => n + 1);
});
var intakeSeconds = wall.Elapsed.TotalSeconds;

// Wait until the dispatcher has drained everything (or 3 minutes).
LatencyDto latency;
var drain = Stopwatch.StartNew();
do
{
    await Task.Delay(1000);
    latency = (await http.GetFromJsonAsync<LatencyDto>($"/sim/dispatch-latency?since={Uri.EscapeDataString(since.ToString("O", CultureInfo.InvariantCulture))}"))!;
}
while (latency.Messages < visits * 0.995 && drain.Elapsed < TimeSpan.FromMinutes(3));
var dispatchSeconds = wall.Elapsed.TotalSeconds;

Console.WriteLine();
Console.WriteLine("## 1. Signed webhook intake and dispatch");
Console.WriteLine($"webhooks: {string.Join(", ", statuses.Select(s => $"{(int)s.Key}×{s.Value}"))}");
Console.WriteLine($"intake throughput: {visits / intakeSeconds:F0} webhooks/s ({intakeSeconds:F1} s)");
Console.WriteLine($"webhook HTTP latency ms: p50 {P(webhookMs, 0.50):F1}  p95 {P(webhookMs, 0.95):F1}  p99 {P(webhookMs, 0.99):F1}  max {webhookMs.Max():F1}");
Console.WriteLine($"dispatched (provider accepted): {latency.Messages} of {visits} in {dispatchSeconds:F1} s → {latency.Messages / dispatchSeconds:F0} messages/s end to end");
Console.WriteLine($"webhook → provider-accepted ms (Oracle PERCENTILE_CONT): p50 {latency.P50Ms:F1}  p95 {latency.P95Ms:F1}  p99 {latency.P99Ms:F1}  max {latency.MaxMs:F1}");

// ---- 2. survey throughput ------------------------------------------------------------------------------
var inbox = (await new SimulatedPatient(http).InboxAsync(inboxStart)).ToList();
var surveyMs = new ConcurrentBag<double>();
var surveyStatuses = new ConcurrentDictionary<HttpStatusCode, int>();
var answers = new VisitGenerator(7);
var plans = inbox.Select(m => (m.Link, Answers: answers.Answers("Cardiology"))).ToList();
wall.Restart();
await Parallel.ForEachAsync(plans, new ParallelOptions { MaxDegreeOfParallelism = concurrency }, async (plan, ct) =>
{
    // One cookie jar per simulated patient (antiforgery cookie), like separate phones.
    using var patientHandler = new SocketsHttpHandler { CookieContainer = new CookieContainer() };
    using var patientHttp = new HttpClient(patientHandler) { BaseAddress = new Uri(baseUrl) };
    var started = Stopwatch.GetTimestamp();
    var status = await new SimulatedPatient(patientHttp).AnswerAsync(plan.Link, plan.Answers, ct);
    surveyMs.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    surveyStatuses.AddOrUpdate(status, 1, (_, n) => n + 1);
});
var surveySeconds = wall.Elapsed.TotalSeconds;

Console.WriteLine();
Console.WriteLine("## 2. Survey completion (GET page + POST answers, new connection per patient)");
Console.WriteLine($"responses: {string.Join(", ", surveyStatuses.Select(s => $"{(int)s.Key}×{s.Value}"))}");
Console.WriteLine($"throughput: {plans.Count / surveySeconds:F0} completed surveys/s ({plans.Count} in {surveySeconds:F1} s)");
Console.WriteLine($"per-survey latency ms (GET+POST): p50 {P(surveyMs, 0.50):F1}  p95 {P(surveyMs, 0.95):F1}  p99 {P(surveyMs, 0.99):F1}");
return 0;

async Task<long> LatestSequenceAsync()
{
    var all = await new SimulatedPatient(http).InboxAsync(0);
    return all.Count == 0 ? 0 : all.Max(m => m.Sequence);
}

static double P(IEnumerable<double> values, double q)
{
    var sorted = values.Order().ToArray();
    if (sorted.Length == 0)
    {
        return double.NaN;
    }

    var rank = q * (sorted.Length - 1);
    var lo = (int)Math.Floor(rank);
    var hi = (int)Math.Ceiling(rank);
    return sorted[lo] + ((sorted[hi] - sorted[lo]) * (rank - lo));
}

internal sealed record LatencyDto(int Messages, double? P50Ms, double? P95Ms, double? P99Ms, double? MaxMs);
