# ADR 0003 — Encrypt personal data in the application with per-contact envelope keys (AES-256-GCM)

- Status: accepted
- Date: 2026-11-07

## Context

The module stores patient names, phone numbers, free-text comments and case notes. Under KVKK, health-related
context is special-category data. "The patient was at the Obstetrics clinic and is unhappy" is health data
attached to a phone number. Threats we want to blunt:

- database dumps, backups and replicas leaking;
- DBAs and reporting users browsing tables;
- one compromised field being moved into another row to be decrypted as someone else's data.

We also need equality lookups (the same patient across visits, the STOP reply from a phone number) and a way to
make data permanently unreadable at the end of its retention period, including in backups we cannot rewrite.

## Options considered

1. **Oracle TDE (tablespace/column).** Protects files at rest and is transparent to queries. But anyone with
   `SELECT` (DBAs, reporting tools, a SQL injection) sees plaintext, and backups exported logically are plaintext.
   It needs Advanced Security (licensed).
2. **DBMS_CRYPTO in PL/SQL.** Keys would have to be reachable from the database session, which defeats the
   "DBA cannot read" goal.
3. **Application-level envelope encryption.** The database stores only ciphertext. Keys live outside the database
   (KMS/HSM in production, configuration in the demo).

## Decision

Option 3 (`PiiProtector`):

- **Per-contact data key (DEK)**, 256-bit random, wrapped with AES-256-GCM by the active **key-encryption key
  (KEK)**. `contact.wrapped_dek` and `contact.kek_id` are stored next to the ciphertext.
- **Fields** are AES-256-GCM with a random 96-bit nonce. Layout: `version | nonce | tag | ciphertext`.
- **Associated data binds ciphertext to its place:** `contact:{id}:{field}` (e.g. `comment:{surveyId}`,
  `note:{caseId}`, `survey-token:{surveyId}`). A ciphertext copied into another row or column fails
  authentication.
- **KEK rotation** re-wraps 48-byte DEKs (`ContactRepository.RewrapKeysAsync`) and leaves field ciphertext
  untouched. The keyring accepts several KEKs; new data uses the active one.
- **Blind indexes** (HMAC-SHA256 with a separate key) replace plaintext lookups:
  - `patient_ref_bidx`, unique, finds a returning patient;
  - `phone_bidx` maps a provider's STOP reply to contacts.
- **Crypto-shredding:** the retention job nulls `wrapped_dek` (ADR 0004). Every remaining copy of that contact's
  ciphertext, including backups, becomes unreadable.
- **Decryption happens only where plaintext is needed:** the phone for the send (dispatcher), the comment and notes
  in the case dialog, and the name and phone on an explicit "reveal" by a case manager. Each of these writes an
  `audit_log` row with actor, fields and purpose. The audit row is in the same transaction as the access, or before
  it.

## Consequences

- The database cannot search inside personal data (`LIKE '%Yılmaz%'` is impossible by design). Search works by
  case, department and score, which is what the panel needs.
- Key custody becomes the critical control. Production must use a KMS/HSM (OCI Vault, Azure Key Vault). The demo
  reads KEKs from configuration, and the compose defaults are development-only (see threat model).
- Ciphertext is larger than plaintext (29 bytes of overhead per field). Column sizes allow for it, and comments are
  capped at 400 characters so the worst case fits `RAW(2000)`.
- Tests prove: round trip (property-based), non-determinism, AAD swap detection, tamper detection, wrong-KEK
  failure, rewrap, and that no plaintext name or phone appears in the stored bytes (Oracle test).
