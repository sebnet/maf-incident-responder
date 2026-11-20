# Phase 1 baseline: single on-call agent

The single `ChatClientAgent` with three read-only telemetry tools, a round-trip budget and structured output,
diagnosing two injected incidents three times each. Every later phase is compared against these numbers.

| | |
|---|---|
| Date | 2026-11-18 |
| Commit | `d8f4a51` (clean) |
| Model | `gpt-4.1-mini`, Azure Data Zone standard ($0.44 in / $0.11 cached in / $1.76 out per 1M tokens) |
| MAF | 1.28.0 |
| Runner | `evals/IncidentResponder.Evals`: settle 5 min, warmup 90 s, tools bounded to injection − 5 min |
| Raw runs | `evals/runs/20261118-112806-phase-1.jsonl` (local, not committed) |

## Results

| Scenario | Runs | Service | Category | Mentions | All correct | Round trips (avg) | Tool calls (avg) | Tokens in / out (avg) | Est. cost (total) | Latency (avg) | Budget hits | Errors |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| s01-catalog-errors | 3 | 3/3 | 3/3 | 3/3 | 3/3 | 3.3 | 3.3 | 5649 / 344 | $0.0083 | 12.2 s | 0 | 0 |
| s02-orders-latency | 3 | 3/3 | 3/3 | 3/3 | 3/3 | 3.0 | 3.0 | 3828 / 314 | $0.0067 | 10.8 s | 0 | 0 |
| s03-db-pool-exhaustion | 3 | 3/3 | **0/3** | 3/3 | **0/3** | 3.0 | 3.0 | 4160 / 309 | $0.0067 | 11.5 s | 0 | 0 |

**Exit criterion met:** both scenarios correct in at least 2 of 3 runs (6/6), cost per diagnosis known:
about **$0.002–0.003** per diagnosis, 10–13 s.

s03 isn't part of the exit criterion. It was run afterwards (2026-11-20, commit `24ed108`: same agent, prompt and
tools; the only change since `d8f4a51` is the runner's JSON escaping) so that it has a Phase 1 number before
Phase 2 adds the runbooks. Raw runs: `evals/runs/20261120-204030-phase-1.jsonl`.

## How the agent investigated

**s01 (catalog-api errors, alert on orders-api).** Always started from the alerting service's error logs, then
the error rate, then followed the dependency: once through catalog-api's logs, every time through a sample trace
(`get_trace`) that showed the `InvalidOperationException` inside catalog-api. Never blamed orders-api, the service
that alerted.

**s02 (orders-api latency, no errors).** The same three steps in all three runs: orders-api p95 → orders-api error
logs (empty) → catalog-api p95 (stable at ~4.8 ms) → "the delay is inside orders-api". No invented errors, and the
dependency ruled out with data rather than assumed. It never looked at a trace: with no error logs it has no
trace id to start from, and no tool to search traces (see below).

**s03 (orders-api Postgres pool exhausted by a background job).** The same three steps in all three runs:
orders-api error logs → error rate → one sample trace. Every run found the right service and the right mechanism
("connection pool exhaustion", `NpgsqlException`, max pool size 20), with confidence 1. Every run labelled it
`ResourceSaturation` instead of `DatabaseConnectivity`, so it scores 0/3. None of them found *what* was holding the
connections (the export job): its start is an Information log and its queries are in spans the agent never opened.
The first suggested action in two runs was "increase Max Pool Size", which the team's runbook explicitly says not
to do.

## Observations

- **s03's wrong category is a schema problem, not a reasoning one.** The definitions the model reads are on
  `Diagnosis.Category`: `DatabaseConnectivity: cannot get or use database connections` and
  `ResourceSaturation: CPU, memory, threads or queues exhausted`. "Pool exhausted" is only in the enum value's own
  `[Description]`, which doesn't reach the schema (`docs/notes/structured-output.md`). So the model saw
  "exhausted" under `ResourceSaturation` and nothing about pools under `DatabaseConnectivity`. Fixing the property
  description is a prompt change: its own commit and eval run, not part of this baseline. The ground truth stays as
  written. A connection pool *is* a saturated resource, so the two categories overlap, and the taxonomy should say
  which one wins.

- **A misattributed evidence source, scored as fully correct.** In s01 #2 the evidence cites
  `[logs:catalog-api] … InvalidOperationException` although that run never queried catalog-api's logs (its tools
  were orders-api logs, error rate, one trace). The fact is true, it came from the trace, but the source is
  invented. The scorer only checks service, category and required terms, so it can't see this.
- **The circuit breaker claim was real.** s01 #2 is the only run mentioning the circuit breaker, and the only one
  whose window had an `OnCircuitOpened` event (checked in Loki). The plain-words description of Polly's events
  (Phase 1, tool design) is doing its job.
- **No trace in s02.** `get_trace` is only reachable through a sample trace id in an error log group. A latency
  incident has none, so the agent reasoned from metrics alone. Enough here (a clean, single-service delay), likely
  not in harder latency cases.
- **Behaviour varies between runs of the same scenario** (s01: 3 or 4 round trips, with or without catalog-api's
  logs) while the outcome stays the same. Expected from a non-deterministic model; only rates are meaningful.
- **Prompt caching** covered 21–32% of input tokens in two s01 runs and none in the others (s02's shorter
  conversations likely stay under the provider's minimum cached prefix).
- **Budget never reached** (max 4 of 10 round trips); no failed tool calls.

## Caveats

- **3 runs per scenario is a small sample.** 6/6 says the agent handles these two cases, not that it is 100%
  accurate.
- **Clean, single-cause synthetic faults.** Published research shows agents scoring far lower on realistic on-call
  inputs than on tidy injected tasks; expect real incidents to be harder.
- **The grader is strict on fields and blind to evidence quality** (see the misattributed source above).

## For the next phases

- Score the evidence, not only the answer: every cited source must correspond to a tool the agent actually called.
- A way to reach traces without error logs (search by service and duration) before adding latency scenarios that
  need it.
- s03: fix the category definitions on `Diagnosis.Category` (say where pool exhaustion goes) and re-run s01–s03,
  since the change can move the other scenarios too. Phase 2 questions for s03: does the runbook stop the "raise
  Max Pool Size" advice, and does anything lead the agent to the job holding the connections?
