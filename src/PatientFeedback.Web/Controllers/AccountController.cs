using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PatientFeedback.Web.Infrastructure;

namespace PatientFeedback.Web.Controllers;

public sealed record DemoUser(string UserName, string DisplayName, string[] Roles, string Description);

/// <summary>
/// Demo sign-in: pick a role and go. It stands in for the hospital's identity provider (OIDC) so the role-based rules
/// — who may handle cases, who may reveal contact details, who may read the audit log — can be tried out.
/// Disabled unless <c>DemoAuth:Enabled</c> is true.
/// </summary>
[AllowAnonymous]
[Route("account")]
public sealed class AccountController(IOptions<DemoAuthOptions> options) : Controller
{
    public static readonly IReadOnlyList<DemoUser> Users =
    [
        new("ayse.agent", "Ayşe (patient relations)", [Roles.Agent], "Handles cases; sees masked contacts only"),
        new("mehmet.manager", "Mehmet (case manager)", [Roles.Agent, Roles.CaseManager], "Handles cases; may reveal contact details to call back"),
        new("selin.dpo", "Selin (data protection officer)", [Roles.PrivacyOfficer], "Reads the personal-data access log"),
    ];

    [HttpGet("login")]
    public IActionResult Login(string? returnUrl = null) =>
        options.Value.Enabled ? View(Users) : NotFound();

    [HttpPost("login")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string userName, string? returnUrl = null)
    {
        var user = Users.FirstOrDefault(u => u.UserName == userName);
        if (!options.Value.Enabled || user is null)
        {
            return NotFound();
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, user.UserName), new Claim("display_name", user.DisplayName), .. user.Roles.Select(r => new Claim(ClaimTypes.Role, r))],
            CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        if (Url.IsLocalUrl(returnUrl))
        {
            return LocalRedirect(returnUrl);
        }

        return user.Roles.Contains(Roles.PrivacyOfficer) ? RedirectToAction("Index", "Audit") : RedirectToAction("Index", "Cases");
    }

    [HttpPost("logout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }

    [HttpGet("denied")]
    public IActionResult Denied() => View();
}

public static class Roles
{
    public const string Agent = "Agent";
    public const string CaseManager = "CaseManager";
    public const string PrivacyOfficer = "PrivacyOfficer";
}

public static class Policies
{
    public const string CaseHandling = "CaseHandling";
    public const string PiiReveal = "PiiReveal";
    public const string AuditRead = "AuditRead";
}
