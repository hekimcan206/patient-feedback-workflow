using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PatientFeedback.Oracle;
using PatientFeedback.Scoring;
using PatientFeedback.Web.Hubs;

namespace PatientFeedback.Web.Controllers;

public sealed class NoteForm
{
    [Required]
    [StringLength(400, MinimumLength = 2)]
    public string Note { get; set; } = "";
}

public sealed class ResolveForm
{
    [StringLength(400)]
    public string? Note { get; set; }
}

public sealed class RevealForm
{
    /// <summary>Why the handler needs the contact details; recorded verbatim in the audit log.</summary>
    [Required]
    [AllowedValues("call-back-patient", "verify-identity", "complaint-escalation")]
    public string Purpose { get; set; } = "";
}

public sealed record MetricView(double? Value, int Responses);

public sealed record DepartmentView(string Department, MetricView Nps, MetricView Csat, MetricView CesMean);

public sealed record StatsView(
    int Days,
    MetricView Nps,
    MetricView Csat,
    MetricView CesMean,
    MetricView CesTopBox,
    IReadOnlyList<DepartmentView> Departments,
    CaseCounts Cases,
    IReadOnlyList<FunnelRow> Funnel);

/// <summary>Case-tracking panel: list and stats as JSON for the grid and chart, actions as validated POSTs.</summary>
[Authorize(Policy = Policies.CaseHandling)]
[Route("cases")]
public sealed class CasesController(
    CaseRepository cases,
    ContactRepository contacts,
    StatsRepository stats,
    ScoringService scoring,
    CaseNotifier notifier,
    TimeProvider clock) : Controller
{
    private string Actor => User.Identity?.Name ?? throw new InvalidOperationException("Authenticated user has no name.");

    [HttpGet("")]
    public IActionResult Index() => View();

    [HttpGet("data")]
    public async Task<IEnumerable<CaseDto>> Data(CancellationToken ct) =>
        (await cases.ListAsync(resolvedLimit: 50, ct)).Select(CaseDto.From);

    [HttpGet("stats")]
    public async Task<StatsView> Stats([FromQuery, Range(1, 365)] int days = 30, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var since = now.AddDays(-days);
        var (overall, byDepartment) = await stats.ScoresAsync(since, scoring, ct);
        var counts = await cases.CountsAsync(new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero), ct);
        var funnel = await stats.FunnelAsync(since, ct);
        return new StatsView(
            days,
            M(overall.Nps),
            M(overall.Csat),
            M(overall.CesMean),
            M(overall.CesTopBox),
            [.. byDepartment.Select(d => new DepartmentView(d.Department, M(d.Scores.Nps), M(d.Scores.Csat), M(d.Scores.CesMean)))],
            counts,
            funnel);

        static MetricView M(AggregateScore s) => new(s.Value, s.Responses);
    }

    /// <summary>Case detail with the patient's comment and handler notes (decrypted; the read is audited).</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> Detail(long id, CancellationToken ct) =>
        await cases.GetDetailAsync(id, Actor, ct) is { } detail
            ? Ok(new { @case = CaseDto.From(detail.Case), detail.Comment, detail.Events, contactAvailable = !detail.Case.ContactAnonymised })
            : NotFound();

    [HttpPost("{id:long}/assign")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Assign(long id, CancellationToken ct) =>
        await AfterChangeAsync(id, await cases.AssignAsync(id, Actor, clock.GetUtcNow(), ct), ct);

    [HttpPost("{id:long}/notes")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddNote(long id, NoteForm form, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(form);
        return !ModelState.IsValid
            ? ValidationProblem(ModelState)
            : await AfterChangeAsync(id, await cases.AddNoteAsync(id, Actor, form.Note, clock.GetUtcNow(), ct), ct);
    }

    [HttpPost("{id:long}/resolve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Resolve(long id, ResolveForm form, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(form);
        return !ModelState.IsValid
            ? ValidationProblem(ModelState)
            : await AfterChangeAsync(id, await cases.ResolveAsync(id, Actor, form.Note, clock.GetUtcNow(), ct), ct);
    }

    /// <summary>
    /// Reveals name and phone so the handler can call the patient back. Requires the case-manager role and a stated
    /// purpose; the audit row commits before the plaintext is returned.
    /// </summary>
    [HttpPost("{id:long}/reveal")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.PiiReveal)]
    public async Task<IActionResult> Reveal(long id, RevealForm form, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        if (await cases.GetAsync(id, ct) is not { } row)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-store";
        return await contacts.RevealAsync(row.ContactId, Actor, form.Purpose, ct) is { } contact
            ? Ok(new { contact.FullName, contact.Phone })
            : Problem(statusCode: StatusCodes.Status410Gone, title: "Contact details were anonymised under the retention policy");
    }

    private async Task<IActionResult> AfterChangeAsync(long id, bool changed, CancellationToken ct)
    {
        if (!changed)
        {
            return Conflict();
        }

        await notifier.CaseChangedAsync(id, ct);
        return NoContent();
    }
}
