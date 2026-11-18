# Evals

The responder is measured against **scenarios with a known root cause**. We inject the fault, so we know the
answer. That is what makes this project's evaluation objective.

## Scenario file (`scenarios/*.json`)

| Field | Meaning |
|---|---|
| `status` | `ready` or `planned` (the runner skips `planned`) |
| `inject.target` / `inject.fault` | Body sent to `POST /_ops/runtime` on the target service |
| `warmupSeconds` | How long to let traffic flow with the fault before asking the agent |
| `alert` | What the agent is told (it is NOT told the target) |
| `expected` | Ground truth: `rootCauseService`, `category`, `mustMention` |

**Rule: ground truth is written by a human, from the injected fault. Never by a model.**

## Runner

`evals/IncidentResponder.Evals` does, per scenario and per repetition:

1. `DELETE /_ops/runtime` on every service, then **settle** `Evals:SettleMinutes` (5) with no fault anywhere.
2. `POST /_ops/runtime` on `inject.target` with `inject.fault`.
3. Wait `warmupSeconds` (the LoadGen keeps traffic flowing).
4. Build a **fresh agent** whose tools never look before *injection − `Evals:BaselineMinutes`* (5), and run
   `DiagnoseAsync` with the `alert`. The bound keeps a run from seeing the previous run's telemetry; the clean
   minutes before the injection are the "normal" the model compares with.
5. `DELETE /_ops/runtime` (always, also on Ctrl+C).
6. Score and write a JSON line to `evals/runs/<date>-<phase>.jsonl` (git-ignored), with the model, the git commit
   (prompts are code) and the MAF version.

Run each scenario **at least 3 times**: the model is not deterministic, so a single run says little.

```bash
# needs the lab running and the same Foundry settings as the CLI (user-secrets are shared)
dotnet run --project evals/IncidentResponder.Evals -- --Evals:Scenarios s01,s02 --Evals:Repetitions 3
```

A run takes about settle + warmup + diagnosis (~7–8 min); the runner prints an estimate before starting.

Each diagnosis is traced to LGTM as service `incident-responder-evals` (the agent's spans under one parent span per
run), and its trace id is in the run's JSON line: open it in Grafana to see what the model received and answered.
The parent span carries only the phase and run number, never the scenario id or the expected answer: the agent's
tools read the same Tempo.

Cost: set the deployment's list prices (USD per million tokens) under `Prices`, keyed by deployment name, from the
Azure pricing page. Without them, cost is reported as unknown, never as 0:

```json
"Prices": { "gpt-4.1-mini": { "InputPerMillion": 0, "CachedInputPerMillion": 0, "OutputPerMillion": 0 } }
```

## Scoring

| Metric | How |
|---|---|
| `service_correct` | `Diagnosis.RootCauseService == expected.rootCauseService` |
| `category_correct` | `Diagnosis.Category == expected.category` |
| `mentions_ok` | every `mustMention` term appears in RootCause or Evidence (case-insensitive) |
| `round_trips`, `tool_calls`, `tools_used` | from the run's messages (`AgentResponse.Messages`) |
| `budget_hits`, `failed_tool_calls` | tool results that are the budget message, or carry an exception |
| `tokens_in/out`, `cost_usd` | `AgentResponse.Usage` (all round trips) × the configured price; cached input priced apart |
| `latency_s` | wall clock of the diagnosis |

A run that fails (no answer, an answer that doesn't fit `Diagnosis`) is recorded with its error and **counts as
wrong**: it is not skipped.

Later (Phase 4): add an LLM-as-judge score for the *quality* of the explanation, kept separate from the objective
metrics above, and compare with the Foundry evaluators.

## Results

After each phase, a short summary goes to `evals/results/phase-N.md` (committed). It shows accuracy and cost per
scenario, so progress across phases is visible and can be charted in the blog.
