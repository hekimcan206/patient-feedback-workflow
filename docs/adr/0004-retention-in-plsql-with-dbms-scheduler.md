# ADR 0004 — Enforce retention with a PL/SQL package run by DBMS_SCHEDULER

- Status: accepted
- Date: 2026-11-07

## Context

KVKK (Law No. 6698, art. 7) requires personal data to be erased, destroyed or anonymised once the purpose for
processing it ends. The Regulation on Erasure, Destruction or Anonymisation of Personal Data (Official Gazette
28.10.2017, No. 30224) requires a controller with a retention and destruction policy to do this at the **first
periodic destruction** after the obligation arises. The periodic interval is set in the policy and **may not exceed
six months** (art. 11). The policy itself is in [kvkk-retention-policy.md](../kvkk-retention-policy.md).

The job has to be reliable (it must run even when no web instance is up), auditable, and safe to run twice.

## Options considered

1. **A hosted background service in the web app.** Familiar, but it runs only while the app runs. Several instances
   would need leader election. The app account would need rights to anonymise everything, so a compromised web tier
   could mass-destroy data.
2. **An external cron calling a CLI.** Another moving part with its own credentials and monitoring.
3. **A PL/SQL package owned by the schema owner, run by `DBMS_SCHEDULER`.** It runs inside the database that holds
   the data, with definer rights. The scheduler keeps run history (`LOGGING_FULL`). The app account gets no execute
   right on it.

## Decision

Option 3 (`db/oracle/schema/03-retention-pkg.sql`):

- **Periods are data** (`retention_policy`):

  | category | period | action |
  |---|---|---|
  | `SURVEY_TOKEN` | link expiry | expire link, destroy queued token |
  | `FREE_TEXT` | 180 days | erase comments and resolved-case notes |
  | `CONTACT_PII` | 365 days after last activity | anonymise contact and shred its key |
  | `AUDIT_LOG` | 730 days | delete access-log rows |

- **Steps:** expire links → erase free text → anonymise contacts → delete old audit rows. Each run writes a
  `retention_run` row with counts and status.
- **Batches with COMMIT per batch** (`p_batch_size`, default 500). Each batch **re-queries** live candidates
  (`FETCH FIRST n ROWS ONLY`) instead of fetching across commits from one open cursor, which avoids ORA-01555 on long
  runs. Updates use `FORALL`. The candidate query matches the function-based index `contact_retention_ix`, which
  holds live contacts only.
- **Idempotent:** every step selects only rows that still need work. A failed run is marked `FAILED` with the error
  and re-raised. The next run continues where it stopped.
- **Legal hold:** a contact with an unresolved case is skipped and counted in `held_open_case`. Resolving the case
  updates `last_activity_at`, so the one-year clock restarts at the resolution.
- **Anonymisation:** nulls the patient reference and phone blind indexes, name, phone, the **wrapped data key**
  (crypto-shredding, ADR 0003) and `kek_id`. Check constraints make a half-anonymised row impossible. Scores,
  department and timestamps remain as anonymous statistics.
- **Audit:** one `PII_ANONYMISE` row per contact, actor `RETENTION_JOB`, `db_user = PF_OWNER`.
- **Scheduling:** `retention_pkg.ensure_job` (re)creates `PF_RETENTION_NIGHTLY` as a `STORED_PROCEDURE` job with
  `FREQ=DAILY;BYHOUR=2;BYMINUTE=30`. That is daily, far inside the six-month ceiling. The installer calls it, and the
  interval is configurable (`Retention:RepeatInterval`).

## Consequences

- Tested against a real Oracle (`RetentionPackageTests`):
  - the 364/366-day boundary and crypto-shredding (a reveal returns nothing);
  - the legal hold and the clock restarting on resolution;
  - link expiry cancelling queued messages and destroying their tokens;
  - 180-day free-text erasure;
  - idempotent re-runs, and batch size 2 processing 7 contacts;
  - audit-log retention;
  - the job definition, `RUN_JOB(use_current_session => TRUE)` producing a `SCHEDULER` run, and an enabled job's
    next run falling within 24 hours.
- The app account cannot run, alter or drop the job (test: `Application_account_cannot_alter_the_audit_trail_or_run_retention`).
- Physical destruction of backups is out of scope for the job. Crypto-shredding covers it for encrypted fields. The
  backup rotation period must still be stated in the policy.
