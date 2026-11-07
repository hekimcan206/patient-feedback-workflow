using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PatientFeedback.Cases;
using PatientFeedback.Oracle;
using PatientFeedback.Scoring;
using PatientFeedback.Security;
using PatientFeedback.Web.Hubs;
using PatientFeedback.Web.Infrastructure;

namespace PatientFeedback.Web.Controllers;

public sealed class SurveyForm
{
    [Required(ErrorMessage = "Lütfen 0–10 arasında bir puan seçin.")]
    [Range(0, 10)]
    public int? Nps { get; set; }

    [Range(1, 5)]
    public int? Csat { get; set; }

    [Range(1, 7)]
    public int? Ces { get; set; }

    [StringLength(SurveyLimits.MaxCommentLength)]
    public string? Comment { get; set; }
}

public sealed record SurveyViewModel(string Token, string Department, SurveyForm Form);

/// <summary>
/// The patient-facing, mobile-first survey (Turkish UI). GET is side-effect free on purpose: WhatsApp and SMS apps
/// fetch links to build previews, so opening must never consume the link — only the POST does.
/// </summary>
[AllowAnonymous]
[EnableRateLimiting(RateLimits.Survey)]
[Route("s")]
public sealed class SurveyController(
    SurveyRepository surveys,
    CaseTriggerPolicy policy,
    CaseNotifier notifier,
    FeedbackMetrics metrics,
    TimeProvider clock) : Controller
{
    [HttpGet("{token}")]
    public async Task<IActionResult> Show(string token, CancellationToken ct)
    {
        NoStore();
        if (!SurveyTokens.TryHash(token, out var hash) || await surveys.FindOpenAsync(hash, clock.GetUtcNow(), ct) is not { } page)
        {
            return Unavailable();
        }

        return View(new SurveyViewModel(token, page.Department, new SurveyForm()));
    }

    [HttpPost("{token}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(string token, SurveyForm form, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(form);
        NoStore();
        if (!SurveyTokens.TryHash(token, out var hash))
        {
            return Unavailable();
        }

        if (!ModelState.IsValid || form.Nps is null)
        {
            var page = await surveys.FindOpenAsync(hash, clock.GetUtcNow(), ct);
            return page is null ? Unavailable() : View("Show", new SurveyViewModel(token, page.Department, form));
        }

        var answers = new SurveyAnswers(form.Nps.Value, form.Csat, form.Ces, form.Comment);
        var trigger = policy.Evaluate(answers);
        var result = await surveys.SubmitAsync(hash, answers, trigger, clock.GetUtcNow(), ct);
        if (result.Status != SubmitStatus.Accepted)
        {
            return Unavailable();
        }

        metrics.Response(NpsScoringService.Classify(answers.Nps).ToString());
        if (result.Case is { } opened)
        {
            metrics.CaseOpened(opened.Severity.ToString());
            await notifier.CaseChangedAsync(opened.CaseId, ct);
        }

        await notifier.SurveyAnsweredAsync(result.Department ?? "", answers.Nps, ct);
        return View("Thanks", result.Case is not null);
    }

    [HttpPost("{token}/opt-out")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> OptOut(string token, CancellationToken ct)
    {
        NoStore();
        if (!SurveyTokens.TryHash(token, out var hash) || !await surveys.OptOutAsync(hash, clock.GetUtcNow(), ct))
        {
            return Unavailable();
        }

        return View("OptedOut");
    }

    // One page for unknown, used, expired and opted-out links, with the same status code: no oracle for guessing.
    private ViewResult Unavailable()
    {
        Response.StatusCode = StatusCodes.Status410Gone;
        return View("Unavailable");
    }

    private void NoStore() => Response.Headers.CacheControl = "no-store";
}
