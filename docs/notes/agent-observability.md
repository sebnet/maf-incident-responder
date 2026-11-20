# Agent observability (OpenTelemetry) — notes

- Phase: 1
- Samples studied: `../agent-framework/dotnet/samples/02-agents/Agents/Agent_Step05_Observability`,
  `02-agents/AgentOpenTelemetry`; source `dotnet/src/Microsoft.Agents.AI/OpenTelemetryAgent.cs`
- MAF version: 1.28.0 (Microsoft.Extensions.AI 10.11.0)

## The problem it solves

A run's `AgentResponse` says what the agent concluded and how many tokens it used in total. It doesn't say *how* it
got there: what each model call received, which call was slow, which tool ran with which arguments, where the tokens
went. Without telemetry you'd add logging in every layer and stitch the lines together by hand. `UseOpenTelemetry`
emits the GenAI semantic-convention spans and metrics, so a run is a trace you can open in Tempo next to the lab's own
traces.

## How it works

One line on the agent, plus a host that subscribes to the source:

```csharp
// OnCallAssistant.Create: emits, never exports
.UseOpenTelemetry(TelemetrySourceName, configure: agent => agent.EnableSensitiveData = sensitiveTelemetry)

// IncidentResponder.Cli/Program.cs: the host chooses where it goes
.WithTracing(tracing => tracing
    .AddSource(OnCallAssistant.TelemetrySourceName)
    .AddHttpClientInstrumentation()
    .AddOtlpExporter(...))                        // → LGTM on :4319
.WithMetrics(metrics => metrics
    .AddMeter(OnCallAssistant.TelemetrySourceName) // token usage, operation duration
    .AddOtlpExporter(...))
```

A run produces three layers of spans:

```
invoke_agent OnCallAssistant(<id>)   one per run: agent name/id/description, input + output messages, total usage
├─ chat gpt-4.1-mini                 one per model round trip: instructions, tool definitions, response format, usage
├─ execute_tool get_error_logs       one per tool call: arguments, result
├─ execute_tool get_metric
├─ chat gpt-4.1-mini
└─ ...
```

- **`invoke_agent` reuses M.E.AI's chat telemetry.** `OpenTelemetryAgent` wraps an `OpenTelemetryChatClient` around a
  forwarding client that calls the inner agent. Then it renames the span and adds the `gen_ai.agent.*` tags. MAF
  doesn't re-implement the convention, so the agent span follows M.E.AI as the spec evolves.
- **The `chat` and `execute_tool` spans come for free.** If the inner agent is a `ChatClientAgent` whose client isn't
  instrumented yet, `UseOpenTelemetry` turns on a telemetry slot *below* the function-calling loop, under the same
  source name. That's why each round trip and each tool gets its own span without a second `UseOpenTelemetry` on the
  chat client.
- **The agent emits, the host exports** (same split as `ILogger`). `.Agents` only names the source
  (`TelemetrySourceName`). The CLI and the eval runner each decide where it goes and what else to collect.
- **`EnableSensitiveData` is the switch for content.** Off: metadata only (operation, model, tokens, durations). On:
  messages, instructions, tool arguments and results. It propagates to the inner chat slot. It's on in the lab
  (`Telemetry:SensitiveData`), and it's the reason a trace shows what the model actually read.

## When NOT to use it

- **Not for the numbers you report.** Token counts and tool calls in `evals/results` come from the `AgentResponse`
  (`RunMetrics`), not from spans. Spans can be sampled, dropped, or exported late. Telemetry is for investigating a
  run, not for scoring it.
- **Sensitive data off anywhere real.** With it on, prompts, telemetry contents and model answers end up in the
  tracing backend with whatever retention and access it has. Fine for the lab, not as a default.
- **Not as a second log.** The middleware's console lines and the spans overlap on purpose (one for the terminal, one
  for Tempo). Don't keep growing both.

## Gotchas

- **The instructions aren't on the `invoke_agent` span.** `invoke_agent` only sees the options passed to the run (none
  here). `ChatClientAgent` adds its instructions later, when it calls the model. So `gen_ai.system_instructions`, the
  tool definitions and the `Diagnosis` schema are on the `chat` spans. Look there to compare prompt versions between
  runs.
- **`invoke_agent` usage is the whole run.** In one s01 trace: 7,247 input tokens (1,920 from cache) and 421 output, the
  sum of four round trips. Per-round-trip numbers are on each `chat` span. That's where you see each round trip
  re-sending a bigger conversation.
- **Order of `Use`.** `UseOpenTelemetry` is the first `Use`, so it's the outermost layer and sees every run, including
  one another layer might stop.
- **The agent's telemetry lands in the Tempo its tools read.** That's intentional (agent traces next to the app's),
  but it means anything on those spans could leak into an investigation. Guards:
  - the agent only reaches traces by id from the lab's error logs, and the responder's logs stay on the console
    (not exported to Loki);
  - the eval runner's parent span carries only the phase and run number, never the scenario id or the expected answer;
  - the eval runner doesn't instrument `HttpClient`, otherwise its `/_ops/runtime` calls (the fault injection) would
    become spans in that same Tempo.
- **No `UseOtlpExporter()`.** It also turns on log export, which would put the agent's logs in Loki next to the logs
  its tools query. Each signal gets its own `AddOtlpExporter`. A programmatic `Endpoint` is used as-is, hence the
  explicit `/v1/traces` and `/v1/metrics` paths.
- **The provider has to be started and flushed.** The CLI calls `host.StartAsync()` (the OTel providers are hosted
  services) and `StopAsync` at the end. The eval runner builds a `TracerProvider` and disposes it. Skip that and the
  last run's spans never arrive.
- **The eval runner exports traces only.** No metrics there: the numbers it needs come from `AgentResponse`.
- **Spans are named after the convention, not your domain.** "Which diagnosis is this?" is answered by the trace id
  stored in each eval run's JSON line, not by a span name.

## Numbers

- Each eval run records its trace id (`evals/runs/*.jsonl`), so any run in `evals/results/phase-1.md` can be opened in
  Grafana.
- One s01 run: 1 `invoke_agent`, 4 `chat`, 5 `execute_tool` spans. 7,247 / 421 tokens, 26% of input served from cache.
- Not measured: the telemetry's own overhead (latency of the exporter, size of spans with sensitive data on). With
  sensitive data on, every `chat` span carries the conversation so far, and `invoke_agent` carries all of it again. So a
  run's spans are several times the size of its conversation.
