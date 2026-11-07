using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PatientFeedback.Oracle;

namespace PatientFeedback.Web.Controllers;

public sealed record AuditPage(IReadOnlyList<AuditRow> Entries, IReadOnlyList<RetentionRunRow> RetentionRuns);

/// <summary>The data protection officer's view: who accessed which personal data and why, and what retention destroyed.</summary>
[Authorize(Policy = Policies.AuditRead)]
[Route("audit")]
public sealed class AuditController(AuditRepository audit, StatsRepository stats) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct) =>
        View(new AuditPage(await audit.RecentAsync(200, ct), await stats.RetentionRunsAsync(10, ct)));
}
