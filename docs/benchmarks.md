# Benchmarks

All numbers on this page were measured on the machine described below. Nothing is estimated or rounded up. The
harness is `benchmarks/PatientFeedback.LoadTest`, run against the docker compose stack.

## What is measured

1. **Webhook intake.** The client sends N signed `visit.completed` webhooks at concurrency C. The client records
   HTTP latency. Each request verifies the HMAC and, in one Oracle transaction, writes contact + event + survey +
   outbox with the PII encrypted.
2. **Webhook → provider accepted.** This is the end-to-end latency: from the moment the app received the event
   (`clinical_event.received_at`) to the moment the fake provider accepted the message (`outbox_message.sent_at`).
   Oracle computes it with `PERCENTILE_CONT` over the stored timestamps (`StatsRepository.DispatchLatencyAsync`,
   exposed as `/sim/dispatch-latency`). It includes:
   - outbox claim (`SKIP LOCKED`);
   - audit write;
   - decryption of phone and token;
   - provider latency (fake, 15–60 ms random);
   - the result update.
3. **Survey completion.** For every delivered link, a separate simulated phone (own connection and cookie jar)
   GETs the page and POSTs the answers. Each POST consumes the token and stores the response; low scores open a case
   and push it over SignalR.

Failure injection stays **on**, as in the demo: WhatsApp throttling 3% (Retry-After 1 s), WhatsApp timeouts 1%,
SMS 503 2%. Numbers ending in 13/14/77/78 trigger permanent failures or opt-outs. Tail latencies therefore include
real retries and channel fallbacks.

## Environment

| | |
|---|---|
| CPU | Intel Core i7-13620H (10 cores / 16 threads), laptop |
| RAM | 15.7 GB host; Docker Desktop VM 7.6 GB / 16 vCPU |
| OS | Windows 11 Home 10.0.26200, Docker Desktop 29.9.1 (WSL2) |
| Database | `gvenzl/oracle-free:23-slim-faststart` (Oracle Database 23ai Free), in Docker |
| App | `patient-feedback-web` container (chiseled .NET 10 runtime, Release build), in Docker |
| Client | load test on the Windows host (.NET 10, Release), over loopback to the published port |
| Host load | 5 containers running during the final runs (the stack plus unrelated idle ones). The machine is shared, so treat the numbers as indicative, not as capacity planning. |
| Data | the database already held ~4,000 surveys and ~1,300 cases from the demo simulator, plus earlier runs |

## Results

### Steady trickle (300 visits, concurrency 1, three runs: median of the three)

| metric | run 1 | run 2 | run 3 | **median** |
|---|---|---|---|---|
| webhook HTTP latency p50 (ms) | 23.6 | 20.6 | 22.0 | **22.0** |
| webhook HTTP latency p95 (ms) | 63.4 | 38.9 | 37.5 | **38.9** |
| webhook → provider accepted p50 (ms) | 100.9 | 103.2 | 108.3 | **103.2** |
| webhook → provider accepted p95 (ms) | 577.7 | 217.8 | 204.8 | **217.8** |
| webhook → provider accepted p99 (ms) | 1,514.3 | 1,703.2 | 1,556.9 | **1,556.9** |

The p99 sits above 1.5 s because about 3% of WhatsApp attempts are throttled with a 1 s Retry-After. This is the
retry policy working as designed.

### Burst (2,000 visits, concurrency 16) — before and after the dispatcher fix

| metric | v0 (batch 32, 8 in flight, audit row per round trip) | **v1 (batch 64, 32 in flight, array-bound audit)** |
|---|---|---|
| webhooks accepted | 2000 × 202 | 2000 × 202 |
| intake throughput | 296 webhooks/s | 256 webhooks/s |
| webhook HTTP latency p50 / p95 / p99 | 32.0 / 82.6 / 711.4 ms | 35.7 / 125.3 / 893.8 ms |
| dispatched (provider accepted) | 1995 of 2000 in 25.7 s | 1994 of 2000 in 11.1 s |
| **end-to-end dispatch throughput** | **77 messages/s** | **180 messages/s** |
| **webhook → provider accepted p50** | **10,128.9 ms** | **1,127.2 ms** |
| webhook → provider accepted p95 / p99 | 17,896.8 / 23,083.1 ms | 2,502.5 / 3,989.7 ms |

Intake throughput is within run-to-run noise. In v1 it shares CPU and Oracle with a dispatcher that is now 2.3×
busier. The few undelivered messages are the generator's permanent-failure numbers on both channels (expected
`DEAD`/opt-out).

**What the v0 run showed.** Intake easily outpaced dispatch, so the outbox queued and latency grew to ten seconds.
The cause was the batch barrier. Each cycle claimed 32 rows, ran them 8 at a time, and audited the 32 phone reads
with Dapper's "one execute per element", which is 32 round trips before the first send. Two changes:

1. The audit batch is written with **ODP.NET array binding**: one round trip for the whole batch
   (`AuditRepository.WriteAsync`).
2. 32 sends in flight and a batch of 64. Provider latency dominates each send, so concurrency is the throughput
   lever.

Real WhatsApp/SMS providers impose their own per-number throughput limits. In production the dispatcher's
concurrency should be set just under the contracted provider rate, not as high as Oracle allows.

### Survey completion (after the burst, concurrency 16)

| metric | value |
|---|---|
| surveys answered | 1994 × 200 (every link once, none rejected) |
| throughput | **225 completed surveys/s** (1994 in 8.9 s) |
| per-survey latency p50 / p95 / p99 (GET page + POST answers) | 61.9 / 120.7 / 190.8 ms |

**Measurement pitfall found on the way.** The first survey run reported 8 surveys/s with a flat p50 of 2,069 ms.
Each simulated phone opened a new connection to `localhost`. On Windows, `localhost` resolves to `::1` first, and the
compose port is published on `127.0.0.1` only, so every new connection waited for the IPv6 attempt to fail (~2 s)
before falling back. Pointing the client at `127.0.0.1` removed the artefact. The numbers above are from that run;
the 8/s figure measured the client, not the system.

## How to reproduce

```bash
docker compose up -d --build                 # wait until the simulator logs "app is ready"
docker compose stop simulator                # keep the load test alone on the box
SURVEY_RATE_LIMIT_PER_MINUTE=1000000 docker compose up -d web   # lift the per-IP survey limit (default 60/min)
SECRET=$(grep -o 'whsec_[^}]*' docker-compose.yml | head -1)    # or your own WEBHOOK_SECRET
dotnet run -c Release --project benchmarks/PatientFeedback.LoadTest -- http://127.0.0.1:18440 "$SECRET" 2000 16
dotnet run -c Release --project benchmarks/PatientFeedback.LoadTest -- http://127.0.0.1:18440 "$SECRET" 300 1
```

Set `WA_RATE_LIMIT_RATE=0 WA_TIMEOUT_RATE=0 SMS_ERROR_RATE=0` before `docker compose up` to measure without failure
injection.
