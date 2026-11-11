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

## Runner (built in Phase 1)

`evals/IncidentResponder.Evals` (console app, to be created) does, per scenario and per repetition:

1. `DELETE /_ops/runtime` on every service (clean state), wait for metrics to settle.
2. `POST /_ops/runtime` on `inject.target` with `inject.fault`.
3. Wait `warmupSeconds` (the LoadGen keeps traffic flowing).
4. Run the responder with the `alert` and get a structured `Diagnosis` (`RunAsync<Diagnosis>`).
5. `DELETE /_ops/runtime`.
6. Score and write a JSON line to `evals/runs/<date>-<phase>.jsonl` (git-ignored).

Run each scenario **at least 3 times**: the model is not deterministic, so a single run says little.

## Scoring

| Metric | How |
|---|---|
| `service_correct` | `Diagnosis.RootCauseService == expected.rootCauseService` |
| `category_correct` | `Diagnosis.Category == expected.category` |
| `mentions_ok` | every `mustMention` term appears in RootCause or Evidence (case-insensitive) |
| `tool_calls` | count, from the tool middleware |
| `tokens_in/out`, `cost_usd` | from the chat client usage + the model's price |
| `latency_s` | wall clock from alert to diagnosis |

Later (Phase 4): add an LLM-as-judge score for the *quality* of the explanation, kept separate from the objective
metrics above, and compare with the Foundry evaluators.

## Results

After each phase, a short summary goes to `evals/results/phase-N.md` (committed). It shows accuracy and cost per
scenario, so progress across phases is visible and can be charted in the blog.
