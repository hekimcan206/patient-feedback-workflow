# ADR 0002 — Dispatch through a transactional outbox in Oracle (SKIP LOCKED claims, leases, fencing)

- Status: accepted
- Date: 2026-11-07

## Context

When a visit event arrives, a survey must be scheduled and a message sent later: two hours after the visit, outside
quiet hours, possibly over two channels with retries. Messaging providers fail in different ways. Throttling and
timeouts are transient. "Not on WhatsApp" and "invalid number" are permanent for that channel. An opt-out means
never again on that channel. The system must never schedule a survey without eventually trying to send it, must
never send for a survey that was rolled back, and must not send the same survey twice.

## Options considered

1. **Send inside the webhook request.** Couples HIS latency to provider latency. There is nowhere to wait two hours,
   and a crash between commit and send loses the message.
2. **In-memory queue / `Channel<T>`.** Fast, but lost on restart and not shareable across instances.
3. **Oracle Advanced Queuing (AQ/TxEventQ).** Transactional with the business tables and a real broker. But it adds
   queue tables, privileges and operational knowledge, and the delay, retry and fallback logic would still live in
   the application. It is worth it at much higher volumes or for many consumers. The `outbox-lab` repository (#18)
   measures it.
4. **Transactional outbox table + polling dispatcher** with `FOR UPDATE SKIP LOCKED`.

## Decision

Option 4.

- `IntakeRepository` writes the contact, the `clinical_event`, the `survey` and the `outbox_message` **in one
  transaction**. The link token is stored encrypted (`token_ct`) until the message is sent, then destroyed.
- `outbox_pkg.claim` (PL/SQL) opens a `FOR UPDATE SKIP LOCKED` cursor over due rows and `FETCH … BULK COLLECT
  LIMIT n`. It marks them `IN_FLIGHT` with a **lease** (`lease_until`) and a random **claim token**. Oracle rejects
  `FOR UPDATE` combined with `FETCH FIRST` (ORA-02014). `ROWNUM <= n … SKIP LOCKED` applies the limit *before*
  skipping, so it under-fills batches. Fetching with `LIMIT` locks rows as they are fetched, which is the correct
  pattern.
- A crashed dispatcher's rows become claimable again when the lease expires. Every later state change is
  **fenced**: `WHERE message_id = :id AND claim_token = :token AND status = 'IN_FLIGHT'`. A worker that lost its
  lease cannot overwrite the newer owner's result.
- The partial index `outbox_due_ix` (function-based, `CASE WHEN status IN ('PENDING','IN_FLIGHT') THEN
  next_attempt_at END`) covers only claimable rows, so the hot index stays small however much history accumulates.
- The decision logic is pure and unit-tested (`DispatchPlanner`):
  - accepted → `SENT`;
  - transient → retry on the same channel, exponential back-off with **full jitter**, never sooner than the
    provider's `Retry-After`, at most 5 attempts per channel;
  - permanent, or attempts exhausted → **fall back** to the next channel;
  - opt-out → record it on the contact (audited), then fall back or stop;
  - nothing left → `DEAD`, and the survey becomes `UNDELIVERABLE`.
- Each send carries an idempotency key (`survey-msg-{id}`). A retry after an ambiguous timeout is de-duplicated by
  the provider (the fake providers implement this), so at-least-once dispatch never reaches the patient twice.
- **Latency:** the poll interval is 1 s, but after committing an immediately due survey the webhook nudges the
  dispatcher through a bounded in-process channel. The poll stays the guarantee for scheduled sends, retries and
  other instances. The nudge only removes idle wait.

## Consequences

- Several app instances can dispatch concurrently. A test proves 4 concurrent claimers of 15 take 40 rows with no
  overlap. Lease expiry and fencing have their own test.
- Sending is at-least-once towards the provider and effectively once towards the patient, given provider-side
  idempotency.
- Measured throughput and latency, the bottleneck found and the fix are in [benchmarks](../benchmarks.md).
- Polling costs one indexed query per second per instance when idle, which is acceptable here. At much higher event
  rates, AQ or a CDC feed would replace the poll.
