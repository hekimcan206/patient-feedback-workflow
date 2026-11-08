# Personal data retention and destruction policy — patient feedback module

> Illustrative policy for this synthetic project, written in the structure KVKK expects. It is not legal advice.
> A real controller adapts the periods to its own legal bases and records them in VERBİS.

**KVKK** is Türkiye's Personal Data Protection Law No. 6698. **VERBİS** is the Data Controllers' Registry.
"Periodic destruction" (*periyodik imha*) means the recurring erasure, destruction or anonymisation, at the
interval set in the controller's policy, of personal data whose processing conditions have all ceased. It is
defined in the Regulation on Erasure, Destruction or Anonymisation of Personal Data (Official Gazette 28.10.2017,
No. 30224).

## 1. Purpose and scope

This policy covers personal data processed to measure patient satisfaction after a visit and to follow up on
negative feedback (service recovery). It applies to the `patient-feedback-workflow` application, its Oracle schema
(`PF_OWNER`), its logs and its backups.

Out of scope: clinical records in the hospital information system (HIS). This module receives no diagnosis, no
national ID number (TCKN) and no clinical notes (data minimisation, enforced by the event contract).

## 2. Data inventory

| Category | Data | Where | Form at rest | Legal basis (illustrative) |
|---|---|---|---|---|
| Identity | full name | `contact.full_name_ct` | AES-256-GCM ciphertext | explicit consent captured at registration (`surveyConsent`) |
| Contact | mobile phone | `contact.phone_ct` | ciphertext | explicit consent |
| Identifier | HIS patient reference | `contact.patient_ref_bidx` | keyed HMAC (blind index), never in clear | legitimate interest in recognising returning patients |
| Contact | phone lookup | `contact.phone_bidx` | keyed HMAC | needed to honour STOP replies |
| Health context | department visited, visit time | `survey`, `clinical_event` | clear (needed for statistics) | explicit consent; becomes anonymous after step 3.3 |
| Opinion | scores, free-text comment | `survey_response` | scores clear, comment ciphertext | explicit consent |
| Service recovery | case notes | `case_event.note_ct` | ciphertext | legitimate interest / complaint handling |
| Access trail | who accessed which record and why | `audit_log` | clear, append-only | legal obligation (data security, art. 12) |
| Link token | survey link | `survey.token_hash` (SHA-256), `outbox_message.token_ct` (until sent) | hash / ciphertext | contract performance (sending the survey) |

Without consent, the module stores **no** personal data. Only the fact that an event arrived is recorded
(`clinical_event.outcome = NO_CONSENT`, `contact_id` NULL).

## 3. Retention periods and actions

Periods are configuration, stored in `retention_policy` and read by `retention_pkg`. The values below are the
installed defaults.

| # | Category | Period | Starts at | Action | Method |
|---|---|---|---|---|---|
| 3.1 | Unused survey link | link lifetime (7 days) | dispatch time | **expire** | token hash and queued encrypted token set to NULL; pending messages cancelled |
| 3.2 | Free text (comments, notes of resolved cases) | 180 days | submission / note time | **erase** | ciphertext set to NULL |
| 3.3 | Contact identity (name, phone, patient ref, phone index) | 365 days | **last activity** (latest visit, answer or case resolution) | **anonymise** | columns set to NULL, **wrapped data key destroyed** (crypto-shredding) |
| 3.4 | Personal-data access log | 730 days | entry time | **delete** | rows deleted |

**Legal hold:** while a contact has an unresolved case, step 3.3 is postponed. The patient may still need to be
called back. Resolving the case is activity, so the 365-day period restarts at the resolution. Every run reports
how many contacts were held (`retention_run.held_open_case`).

**What remains after anonymisation:** scores, department, dates and the case record (severity, reasons, status,
SLA) with an anonymous contact stub. This data no longer relates to an identifiable person and falls outside KVKK.

## 4. Periodic destruction

- **Interval:** daily at 02:30 (database time zone, UTC in the container), via the Oracle `DBMS_SCHEDULER` job
  `PF_RETENTION_NIGHTLY`. This is far inside the regulation's ceiling of **six months** between periodic
  destructions.
- **Mechanism:** `retention_pkg.run` (PL/SQL, definer rights of `PF_OWNER`). It processes contacts in batches of 500
  with a commit per batch, re-queries each batch (restartable, no fetch across commits) and is idempotent.
- **Least privilege:** the web application's database account (`PF_APP`) has no EXECUTE on the package and no DELETE
  on any table. It cannot trigger, alter or bypass destruction.
- **Records:** each run writes a `retention_run` row (start, end, as-of time, counts per step, status, error). Each
  anonymised contact gets a `PII_ANONYMISE` audit row with actor `RETENTION_JOB`. The data protection officer's page
  (`/audit`) lists recent runs. The scheduler keeps its own run history (`LOGGING_FULL`).
- **Failure handling:** a failed run is marked `FAILED` with the error and re-raised (visible in the scheduler log).
  The next nightly run continues from where it stopped.

## 5. Backups and copies

Encrypted fields in backups become unreadable once step 3.3 destroys the contact's data key. The key is stored only
in the live row, and KEKs (key-encryption keys) never enter the database. Backup media must still be rotated
within the controller's backup retention period (to be defined per installation, recommended ≤ 35 days).
Application logs never contain personal data: request paths are logged by route template, so survey tokens are not
logged either.

## 6. Requests from data subjects

An erasure request under KVKK art. 11 is served by setting the contact's `last_activity_at` back past the period and
running `retention_pkg.run` (or by a dedicated workflow; see the `kvkk-dsar` repository). An opt-out ("I don't want
these messages", or a STOP/IPTAL reply) is honoured immediately per channel and recorded in the audit log.

## 7. Verification

The policy is enforced by code and checked by automated tests against a real Oracle
(`tests/PatientFeedback.OracleTests/RetentionPackageTests.cs`):

- day 364 keeps, day 366 anonymises and shreds;
- legal hold, and the clock restarting at resolution;
- link expiry;
- 180-day free-text erasure;
- idempotent re-runs and batch boundaries;
- audit-log retention;
- the scheduler job's definition, next run within 24 hours, and an end-to-end `DBMS_SCHEDULER.RUN_JOB`.

## 8. Roles

| Role | Responsibility |
|---|---|
| Data protection officer (`PrivacyOfficer` role) | reviews the access log and retention runs monthly; owns this policy |
| Patient relations (`Agent`) | handles cases; sees masked contacts only |
| Case manager (`CaseManager`) | may reveal contact details for a stated purpose (audited) |
| DBA | owns `PF_OWNER`, the scheduler job and backup rotation; cannot read encrypted fields |

## 9. Review

Reviewed at least yearly, and whenever a new data category, recipient or legal basis is added.
