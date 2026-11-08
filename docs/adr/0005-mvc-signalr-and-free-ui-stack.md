# ADR 0005 — ASP.NET Core MVC + SignalR with a free UI stack (no DevExtreme), on .NET 10

- Status: accepted
- Date: 2026-11-07

## Context

The production stack this project mirrors is ASP.NET Core MVC on **.NET 8**, Dapper over Oracle, and
DevExtreme-style grid screens with live updates. A public portfolio repository cannot ship a commercial UI licence,
and it should run on the current LTS-track SDK used by the other repositories (.NET 10).

## Options considered

1. **DevExtreme** — the closest match, but commercial. Its trial cannot be redistributed in a public repository.
2. **Blazor Server / a SPA (React)** — modern, but it moves away from the MVC + Razor + Dapper architecture being
   demonstrated, and adds a second build pipeline.
3. **MVC + Razor + small MIT libraries** — Tabulator (grid with sorting and header filters), Chart.js (bar chart),
   and the `@microsoft/signalr` browser client.

## Decision

Option 3.

- Server-rendered Razor views. All JavaScript is in `wwwroot/js` with **no inline script**, so the CSP is
  `script-src 'self'`.
- Libraries are vendored into `wwwroot/lib` (versions and licences in `THIRD-PARTY-NOTICES.txt`), so the panel works
  on a hospital intranet without CDN access.
- **SignalR is used server → client only.** Changes go through MVC actions, which authorise, validate (antiforgery
  plus model validation), audit, and then broadcast a PII-free `CaseDto`. A hub that accepted commands would need
  all of that duplicated. After a reconnect the client reloads once to catch pushes it missed.
- The survey page is server-rendered HTML that works without JavaScript. It is mobile-first, in Turkish, with large
  tap targets. GET is side-effect free, because messaging apps prefetch links for previews.
- `net10.0` everywhere. Nothing used is .NET 10-specific, apart from `Base64Url` in the token helper (available since
  .NET 9) and C# 14 conveniences, so the code ports to the .NET 8 production line with minor edits.

## Consequences

- The case panel looks like a data-grid business screen and updates live (see the README screenshot), with no
  licence cost.
- Demo sign-in (pick a role) stands in for the hospital identity provider (OIDC in production). Role policies are
  real: `Agent` / `CaseManager` for case handling, `CaseManager` only for revealing contacts, `PrivacyOfficer` for the
  access log. The E2E tests cover a forbidden reveal.
- A single instance is assumed for SignalR. Scaling out needs a backplane (Redis, or Azure SignalR); see
  `signalr-scale-lab`.
