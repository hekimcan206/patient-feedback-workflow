# Security policy

## Supported versions

Only the latest release receives fixes.

## Reporting a vulnerability

Please **do not open a public issue**. Use GitHub's private vulnerability reporting
(**Security → Report a vulnerability**). Expect an acknowledgement within 5 working days and a fix or
mitigation plan within 30 days of confirmation.

## Data

This project contains **synthetic data only**. Never submit real patient names, phone numbers, comments or hospital
data in issues, pull requests, test fixtures or screenshots. See `docs/threat-model.md` and
`docs/kvkk-retention-policy.md`.

## Security-relevant defaults

| Area | Default |
|---|---|
| Inbound events | Standard Webhooks HMAC-SHA256, ±5 min window, persisted message-id de-duplication, constant-time compare, rotation via secret lists, 16 KB body cap (ADR 0001) |
| Personal data at rest | AES-256-GCM envelope encryption per contact, KEK outside the database, AAD binding, blind indexes for lookups (ADR 0003) |
| Survey links | 256-bit tokens, SHA-256 stored, single use enforced by a conditional UPDATE, uniform 410 for unknown/used/expired, per-IP rate limit, `Referrer-Policy: no-referrer`, `no-store` |
| Audit | every PII read/write/reveal/opt-out/anonymisation; `PF_APP` can INSERT and SELECT only |
| Database | `PF_APP`: object grants only, no DELETE, no EXECUTE on retention; DDL and the scheduler job belong to `PF_OWNER`, used only by the one-shot `--init-oracle` process |
| Retention | nightly `DBMS_SCHEDULER` job, crypto-shredding of data keys (ADR 0004) |
| Web | antiforgery on all POSTs, SameSite=Strict auth cookie, CSP `script-src 'self'` (no inline script), `X-Frame-Options: DENY` |
| Container | chiseled runtime image, non-root UID 1654, no shell |
| Supply chain | central package versions; vulnerable transitive `System.Security.Cryptography.Pkcs` pinned to a patched version; vendored JS libraries listed with versions; updated by hand; CI fails on known-vulnerable direct or transitive packages (`dotnet list package --vulnerable --include-transitive`); CodeQL `security-and-quality` |
| Secrets | none for production in the repository; compose defaults and `appsettings.Development.json` keys are development-only and overridable via `.env` |

Not included: a real identity provider (demo sign-in stands in for OIDC), TLS termination, KMS/HSM integration.
