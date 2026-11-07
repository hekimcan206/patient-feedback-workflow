using PatientFeedback.Oracle;
using PatientFeedback.Web.Dispatch;

namespace PatientFeedback.Web.Simulation;

/// <summary>
/// Endpoints that exist only for the demo, the load test and the end-to-end test (Simulation:Enabled=true):
/// the fake providers' handset inbox and the dispatch-latency read-out.
/// </summary>
internal static class SimulationEndpoints
{
    public static IEndpointRouteBuilder MapSimulationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var sim = endpoints.MapGroup("/sim").AllowAnonymous();

        sim.MapGet("/handset", (FakeHandset handset, long after = 0, int max = 500) =>
            Results.Ok(handset.After(after, Math.Clamp(max, 1, 5000))));

        sim.MapGet("/dispatch-latency", async (StatsRepository stats, TimeProvider clock, DateTimeOffset? since, CancellationToken ct) =>
            Results.Ok(await stats.DispatchLatencyAsync(since ?? clock.GetUtcNow().AddHours(-1), ct)));

        return endpoints;
    }
}
