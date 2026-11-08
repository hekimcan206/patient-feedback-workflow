# Contributing

## Prerequisites

- .NET SDK 10.0.400 or later (`global.json` rolls forward to the latest feature band)
- Docker (the Oracle tests start `gvenzl/oracle-free:23-slim-faststart` through Testcontainers; `docker compose up` runs the demo)

## Workflow

1. Open an issue first for anything larger than a small fix.
2. Branch from `main`; one concern per pull request.
3. Before pushing:

   ```bash
   dotnet build                                   # warnings are errors
   dotnet test                                    # unit + property + Oracle Testcontainers + end-to-end
   dotnet format --verify-no-changes
   ```

## Changing the schema or PL/SQL

- Scripts live in `db/oracle/schema` and are embedded into `PatientFeedback.Oracle`; the installer, the tests and the
  compose installer apply the same files.
- Tables are created only on an empty schema. A change to an existing table needs a migration script, not an edit of
  `01-tables.sql` (out of scope for v1, see README limitations). Packages are `CREATE OR REPLACE` and re-applied on
  every install.
- Every change to `retention_pkg` needs a test in `RetentionPackageTests` and, if a period or action changes, an update
  of `docs/kvkk-retention-policy.md` in the same pull request.
- The application account (`PF_APP`) gets object grants in `04-grants.sql` only. Never grant DELETE or EXECUTE on
  `retention_pkg` to it; a test enforces this.

## Personal data

Synthetic data only. New personal-data fields must be encrypted with `PiiProtector` (with a field-specific associated
data string), audited on read and covered by the retention package.

## Commit messages

[Conventional Commits](https://www.conventionalcommits.org/) keep the history readable and make the hand-written
CHANGELOG easy to assemble at release time
(`feat:`, `fix:`, `perf:` are user-visible; `docs:`, `test:`, `build:`, `ci:`, `refactor:`, `chore:` are not).

## Architecture decisions

A change to a decision in `docs/adr/` needs a new ADR that supersedes the old one (MADR format).
