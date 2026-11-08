# Threat model

Scope: the web app (webhook intake, dispatcher, survey pages, case panel), the Oracle schema, and the messaging
provider boundary. Method: STRIDE per trust boundary. The method is lightweight; the aim is to state which control
answers which threat and where it is tested.

## Assets

| Asset | Why it matters |
|---|---|
| Patient name and phone, with the department visited | special-category context under KVKK; misuse enables targeted fraud ("the hospital calling about your results") |
| Comments and case notes | free text, may contain health details |
| Survey link tokens | each one lets someone answer as that patient |
| Webhook signing secrets, KEKs, blind-index key | compromise defeats authenticity or confidentiality at scale |
| Audit log | evidence of who accessed what; must not be alterable by the actors it records |
| Hospital reputation / messaging sender identity | a forged event makes the hospital message people |

## Trust boundaries and actors

```
HIS ──signed webhook──▶ [web app] ──fake WhatsApp/SMS──▶ patient phone ──GET/POST link──▶ [web app]
                           │  ▲                                        provider STOP callback (signed) ─▶
                           ▼  │  PF_APP (DML on business tables, INSERT/SELECT audit, EXECUTE outbox_pkg)
                        [Oracle PF_OWNER schema] ◀── DBMS_SCHEDULER retention job (definer rights)
staff browser ──cookie (SameSite=Strict) + antiforgery──▶ case panel / SignalR hub
```

Actors: the HIS (trusted sender holding a secret); patients (anonymous holders of a token); staff (agent, case
manager, DPO); providers; an external attacker; a curious insider with database access.

## Threats and mitigations

| # | STRIDE | Threat | Mitigation | Evidence |
|---|---|---|---|---|
| T1 | Spoofing | forged `visit.completed` makes the hospital message arbitrary numbers | Standard Webhooks HMAC-SHA256 over id.timestamp.raw-body; JSON parsed only after verification; 16 KB cap | ADR 0001; `WebhookVerifierTests`, `WebSurfaceTests` |
| T2 | Tampering / replay | captured request replayed later or immediately | ±5 min timestamp window; `webhook-id` = primary key of `clinical_event`; one survey per visit (`survey_visit_uk`) | replay tests; concurrent-duplicate Oracle test |
| T3 | Info disclosure | timing side channel on signature compare | fixed-length decode + `FixedTimeEquals`, no early exit across secrets | timing test |
| T4 | Spoofing | provider callback forged to opt everyone out | separate provider secret list, same verifier | `WebhooksController.Inbound` |
| T5 | Elevation | guessing or enumerating survey links | 256-bit CSPRNG tokens; only SHA-256 stored; unknown, used and expired links all return the same 410 page; per-IP rate limit | `SurveyTokenTests`, `WebSurfaceTests` |
| T6 | Tampering | double submission / race on one link | conditional `UPDATE … WHERE token_hash = :h AND status = 'SENT'` consumes atomically | concurrent-submit Oracle test (6 parallel → 1 accepted) |
| T7 | Info disclosure | link leaks via Referer, caches or logs | `Referrer-Policy: no-referrer`, `Cache-Control: no-store`, request logs use the route template, not the path; GET never consumes (link previews) | `WebSurfaceTests` |
| T8 | Info disclosure | DB dump, backup, DBA or reporting user reads PII | app-level AES-256-GCM envelope encryption, KEKs outside the DB, blind indexes for lookups | ADR 0003; "no plaintext at rest" Oracle test |
| T9 | Tampering | ciphertext swapped between rows/fields | AEAD associated data `contact:{id}:{field}` | `PiiProtectorTests` |
| T10 | Repudiation | staff read or reveal data without trace | `audit_log` row for every PII read, write, reveal, opt-out and anonymisation, in the same transaction or before the access; `db_user` default column | E2E test asserts audit rows |
| T11 | Tampering | audit trail altered | `PF_APP` has INSERT/SELECT only; deletion only by the retention package after 730 days | `Application_account_cannot_alter_the_audit_trail_or_run_retention` |
| T12 | Elevation | agent reveals contacts beyond their role | role policies; reveal requires `CaseManager` + a purpose from an allow-list | E2E forbidden-reveal test |
| T13 | Spoofing (CSRF) | staff tricked into resolving or revealing | antiforgery tokens on all POSTs, SameSite=Strict cookie, CSP `form-action 'self'` | MVC `[ValidateAntiForgeryToken]` |
| T14 | Info disclosure (XSS) | patient comment rendered as HTML in the panel | Razor encoding; panel JS escapes all strings (`text()`); CSP `script-src 'self'` with no inline script | code review; CSP header test |
| T15 | DoS | webhook or survey floods | body cap, per-IP survey rate limit, bounded dispatcher concurrency, provider timeouts | config |
| T16 | Elevation | compromised web tier mass-destroys data | `PF_APP` has no EXECUTE on `retention_pkg`, no DELETE anywhere | grants script + test |
| T17 | Info disclosure | data kept longer than needed | nightly `DBMS_SCHEDULER` retention with crypto-shredding | ADR 0004; retention tests |
| T18 | Info disclosure | personal data over SignalR to every connected client | hub payloads are PII-free (`CaseDto`); hub requires authentication | `CaseHub` |

## Accepted risks and next steps

- **Key custody in the demo:** KEKs and secrets come from configuration (compose defaults are development-only).
  Production: KMS/HSM-backed keys, secrets from a vault, rotation runbooks.
- **Demo sign-in** stands in for OIDC. Production: hospital IdP, MFA for `CaseManager`/`PrivacyOfficer`.
- **ASP.NET Data Protection keys** (cookies, antiforgery) are ephemeral per container. Multi-instance deployments
  need a shared, protected key ring.
- **TLS** is expected at the ingress; HSTS is not set by the app.
- **Fake providers:** real adapters must verify provider callback signatures with the provider's own scheme, and
  must not log message bodies (they contain the link).
