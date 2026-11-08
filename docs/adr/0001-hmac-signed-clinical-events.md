# ADR 0001 — Verify clinical events with Standard Webhooks HMAC signatures

- Status: accepted
- Date: 2026-11-07

## Context

The hospital information system (HIS) pushes a `visit.completed` event for every finished visit. The event carries
personal data (name, phone, patient reference) and starts a process that contacts the patient. Anyone who can post
a forged or replayed event can make the hospital message arbitrary phone numbers in its name, or make the same
patient receive the survey again and again. The endpoint is reachable from the HIS network. We cannot assume mTLS
on every HIS deployment, so the payload has to authenticate itself.

What the receiver must guarantee:

1. **Authenticity and integrity.** The bytes were produced by a holder of the shared secret and were not changed.
2. **Freshness.** A captured request cannot be replayed later.
3. **Exactly-once effect.** A retry, or a replay inside the freshness window, does not schedule a second survey.
4. **No timing oracle.** Response time does not tell an attacker how much of a guessed signature was right.
5. **Rotation without downtime.** Secrets can be changed without a flag day between the HIS and us.

## Options considered

1. **Static API key header.** Simple, but it authenticates only the caller. It says nothing about the body, it is
   replayable for ever, and it is often logged by proxies.
2. **Custom HMAC scheme** (`X-Signature: hex(hmac(body))`). Covers integrity, but every such scheme has to re-decide
   what is signed, the encoding, the replay window and rotation. Body-only signing is replayable.
3. **Standard Webhooks** ([spec](https://github.com/standard-webhooks/standard-webhooks/blob/main/spec/standard-webhooks.md)):
   `webhook-id`, `webhook-timestamp`, `webhook-signature: v1,<base64 HMAC-SHA256(secret, "id.timestamp.body")>`,
   `whsec_` secrets of 24–64 random bytes, and several space-separated signatures for rotation. Libraries exist in
   most languages, so the HIS vendor can sign with an off-the-shelf package.
4. **Asymmetric signatures** (Ed25519, `v1a`). No shared secret on our side. More key management than this
   integration needs today; the format leaves room to add it later.

## Decision

Option 3, implemented in `WebhookVerifier` (Core, no dependencies):

- **Order of checks:** size limit (16 KB) → headers present → timestamp parses → timestamp within **±5 minutes** →
  signature. JSON is parsed only after the signature verifies, so an unauthenticated body is never deserialised.
- **What is signed:** `id.timestamp.` followed by the **raw request bytes**. We do not re-serialise. Re-serialising
  would break on the first whitespace or key-order difference.
- **Constant-time comparison:** each candidate is base64-decoded into a fixed 32-byte buffer and compared with
  `CryptographicOperations.FixedTimeEquals`. All (secret × signature) pairs are compared with no early exit.
  Wrong lengths, unknown versions and bad base64 never reach the comparison.
- **Replay inside the window:** the signature check alone cannot stop it. The `webhook-id` is the **primary key of
  `clinical_event`**. A second delivery with the same id hits a fast-path lookup, or the unique key if the two
  deliveries race. It is answered `202 DuplicateDelivery` without side effects. A second event for the same visit
  under a new id is stopped by `survey_visit_uk`. Both are persisted guarantees, not an in-memory cache, so they
  survive restarts and work across instances.
- **Rotation:** `Webhooks:ClinicalEventSecrets` is a list. Procedure: (1) add the new secret on the receiver;
  (2) the HIS signs with new and old (two signatures in one header); (3) the HIS drops the old one; (4) remove the
  old one on the receiver. Tests cover both overlap directions.
- **Error responses:** every signature failure returns the same `401 Invalid webhook signature`. The specific verdict
  goes to the log and the `pf.webhooks` metric only.
- Provider inbound callbacks (`STOP` replies) use the same verifier with a **separate secret list**, so a leaked
  provider secret cannot forge clinical events.

## Consequences

- Tests in `WebhookVerifierTests`: the tolerance boundaries (±300 s accepted, ±301 s rejected), a capture replayed
  an hour later, a tampered byte, a changed id or timestamp, unknown secrets, malformed headers, both rotation
  directions, and a **timing test**. The timing test compares median rejection time for a signature wrong in its
  first byte against one wrong in its last byte, over interleaved rounds, with a loose bound that would catch an
  early-exit comparison. Oracle tests prove that 8 concurrent deliveries of one message id schedule exactly one
  survey.
- Clock skew between the HIS and us must stay under 5 minutes (NTP). Rejections are counted by verdict, so skew shows
  up as a `TimestampTooOld` / `TimestampInFuture` spike.
- The shared secret is as sensitive as a password. It lives in configuration or a secret store, never in the
  repository (the compose defaults are development-only).
