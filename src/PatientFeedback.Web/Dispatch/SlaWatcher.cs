using Microsoft.Extensions.Options;
using PatientFeedback.Oracle;
using PatientFeedback.Web.Hubs;
using PatientFeedback.Web.Infrastructure;

namespace PatientFeedback.Web.Dispatch;

/// <summary>Flags cases whose SLA has passed and pushes the change to the panel.</summary>
public sealed partial class SlaWatcher(CaseRepository cases, CaseNotifier notifier, IOptions<DispatcherOptions> options, TimeProvider clock, ILogger<SlaWatcher> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.SlaCheckInterval, clock);
        do
        {
            try
            {
                foreach (var caseId in await cases.MarkBreachesAsync(clock.GetUtcNow(), stoppingToken))
                {
                    LogBreach(caseId);
                    await notifier.CaseChangedAsync(caseId, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // Keep watching after a transient database error.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogFailed(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Case {CaseId} breached its SLA")]
    private partial void LogBreach(long caseId);

    [LoggerMessage(Level = LogLevel.Error, Message = "SLA check failed")]
    private partial void LogFailed(Exception ex);
}
