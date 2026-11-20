# Tool design — notes

- Phase: 1
- Samples studied: `../agent-framework/dotnet/samples/01-get-started/02_add_tools`
- MAF version: 1.28.0 (Microsoft.Extensions.AI 10.11.0)

## The problem it solves

The model can't open Grafana. Everything it knows about the incident comes from what the tools return, and raw
telemetry is the worst possible input: thousands of log lines, a sample per second, traces with dozens of spans,
library jargon. Dump that into the conversation and the model drowns, the important line gets buried, and every later
round trip pays for it again. Tool design is deciding what the model gets to see.

## How it works

A tool is a C# method. `AIFunctionFactory.Create` builds its schema from the signature and the `[Description]`
attributes, so the model sees the name, the description of the tool and of each parameter, and nothing else of the code:

```csharp
// TelemetryTools
[Description("Top error log groups for a service: messages grouped and counted, with first/last seen and one sample " +
             "trace id per group. Use it first to see WHAT is failing.")]
public Task<LogSummary> GetErrorLogsAsync(
    [Description("Service name, e.g. 'orders-api' or 'catalog-api'")] string service,
    [Description("Look-back window in minutes (1-120). Default 15.")] int minutes = 15,
    CancellationToken cancellationToken = default) => ...
```

When the model asks for a tool, the function-calling loop runs the method and serializes the result to JSON. That JSON
is added to the conversation: **a tool's output is the model's next input**, and it's re-sent on every later round trip
of the run. So it deserves the same care as the prompt.

The rules this repo follows (AGENTS.md §6.4):

- **Bounded.** Every query has a time window (1–120 min, clamped whatever the model asks) and a top-N. The eval runner
  can also set a floor (`notBefore`) so a run never sees the previous one's telemetry. The window actually used is part
  of the result, so the model knows what it looked at.
- **Summaries, not dumps.** Each tool does in code what code does better than a model:
  - logs (`LokiLogs`): grouped by message template + exception + request + dependency, ids normalized
    (`/items/3` → `/items/{n}`), counted, top-N, one sample trace id per group;
  - metrics (`PrometheusMetrics`): min/max/avg/last plus at most 30 points, rounded;
  - traces (`TempoTraces`): total duration, the 5 slowest spans, the error spans (max 20).
- **Facts, not jargon.** The useful part is often not the log line itself: "An unhandled exception has occurred while
  executing the request." says nothing, the exception type and message do. Library lines are rewritten in plain words:

  ```
  before: Execution attempt. Source: '-standard//Standard-Retry', Operation Key: '', Result: '500', Handled: 'True', Attempt: '3', ...
  after:  outgoing call failed, retries exhausted: GET https+http://catalog-api/items/{n} -> 500 after 4 attempts | request: /orders
  ```

  Client spans are named after the service that answered (`GET /items/1 -> catalog-api`), not just `GET`.
- **Descriptions say what it returns and when to use it** ("WHAT is failing", "WHEN it started and HOW BAD",
  "WHERE in the call chain").
- **Validate before calling the backend.** A malformed trace id is rejected before it reaches Tempo's URL: arguments
  come from the model.
- **Read-only tools are free; anything that changes the system needs approval** (Phase 3).

## When NOT to use it

- **Don't expose the raw query language** (a `run_logql` tool). It's tempting because it's flexible, but the model
  writes wrong queries, gets dumps back, and you lose the summarization. A 2026 study found high-level diagnosis tools
  nearly matched raw access in accuracy in less than half the time, and that adding *more* tool levels can lower
  accuracy.
- **Don't make the model compute.** Counting, grouping, percentiles and comparisons belong in the tool; the model
  interprets the result.
- **Don't add tools for facts that never change.** Put them in the instructions (today: the three services and who
  calls whom). Tools are for what has to be looked up.
- **Don't add overlapping tools.** Two tools that answer almost the same question make the choice harder and the runs
  less consistent.

## Gotchas

- **Unclear output gets "completed" by the model.** With the raw Polly line, the model reported "circuit breaker
  opened" in 2 of 2 runs. The rewrite stopped it... and then Loki showed the breaker *does* open intermittently in s01.
  The model was probably right. Check the data before calling something a hallucination: the baseline run that
  mentioned the breaker is the only one whose window had an `OnCircuitOpened` event.
- **A tool can be unreachable.** `get_trace` needs a trace id, and the only source of trace ids is the error logs. In
  s02 (latency, no errors) the agent never looked at a trace. It was right anyway, from metrics, but a latency case
  that needs a trace would fail for lack of a tool, not of reasoning.
- **Evidence can cite a tool that wasn't called.** In one s01 run the diagnosis cites catalog-api's logs although it
  never queried them (the fact came from a trace). True content, invented source; field-based scoring can't see it.
- **The result reaches the model as indented JSON.** `AIFunctionFactory` serializes the return value into an indented
  `JsonElement` before middleware sees it: about 20% of a small result is whitespace. Whether the indentation travels to
  the model depends on the provider adapter (not verified).
- **The model chooses the window.** The same scenario got 10, 15 and 30 minutes across runs. Defaults and clamping
  matter more than the description's advice.
- **Exception fields depend on the language.** The grouping by message template (`_OriginalFormat_`) and the
  exception fields are .NET specifics; another stack needs its own summarization.

## Numbers

- Tool results in the Phase 1 baseline: 160–1,960 characters (~40–490 tokens); the warning threshold is 4,000.
- A diagnosis read 3–4 tool results and used ~3,800–5,600 input tokens in total, prompt and tool definitions included
  (`evals/results/phase-1.md`).
- Not measured: the same runs with raw, unsummarized tool output. A before/after on that would be the strongest
  argument for this note.
