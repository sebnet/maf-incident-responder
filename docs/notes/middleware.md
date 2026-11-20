# Middleware — notes

- Phase: 1
- Samples studied: `../agent-framework/dotnet/samples/02-agents/Agents/Agent_Step11_Middleware`
- MAF version: 1.28.0 (Microsoft.Extensions.AI 10.11.0)

## The problem it solves

An agent run is a loop the model drives: it decides how many round trips to make, which tools to call and with what
arguments. Some rules should hold however the model behaves: a run can't go on forever, every tool call gets logged,
and a tool that dumps too much gets flagged. Without middleware you would put those rules inside every tool, or wrap
the `IChatClient` and work out tool calls from raw messages. Middleware runs your code around a run, a model call or a
tool call, with no change to the agent or the tools.

## How it works

There are three kinds, one per layer, and each sees something different (sample `Agent_Step11_Middleware`):

| Kind | Wraps | Signature gets | Typical use |
|---|---|---|---|
| Agent run | the whole `RunAsync` | messages, session, options, inner agent | guardrails, PII filtering, HITL loops |
| Function call | each tool invocation | `FunctionInvocationContext` (function, arguments, `Iteration`), `next` | logging, budgets, overriding results |
| Chat client | each model round trip | messages, `ChatOptions`, inner client | inspecting or changing what goes to the model |

Agent-level middleware is added with `agent.AsBuilder().Use(...)`. Chat-client middleware is added on the client
before `AsAIAgent` (or through `clientFactory`). Middleware can also be per run (`ChatClientAgentRunOptions.ChatClientFactory`).
Every kind is a delegate that calls `next` / the inner agent / the inner client, or doesn't: **not calling it is a short-circuit.**

This repo uses one function-call middleware, `ToolCallMiddleware`, with three jobs:

```csharp
// ToolCallMiddleware.InvokeAsync
var roundTrip = context.Iteration + 1; // 1-based, as a human counts
if (roundTrip > _budget.MaxRoundTrips)
{
    return $"{BudgetExhaustedMessage} (...). This tool was not run. " +
           "Do not call more tools. Answer now with the evidence you already have.";
}

result = await next(context, cancellationToken);   // the tool runs here
var chars = ResultChars(result);                   // compact JSON length = what the model reads
LogSucceeded(...);                                 // tool, args, ms, chars, ~tokens
if (chars > _budget.WarnResultChars) LogLarge(...); // warn, never cut
return result;
```

- **Round-trip budget.** The cost of a run is the number of round trips × what each one re-sends. The budget limits
  the first. Over the limit the tool doesn't run. The model gets a *tool result* telling it to answer with what it
  has, so the run still ends with a diagnosis, not an exception.
- **Result size.** The size of each result in characters, plus a rough token estimate, logged per call. It shows the
  second factor, and the warning catches a tool that starts dumping. Results are never cut: keeping them small is the
  tool's job (`tools-design.md`).
- **Logging.** One line per tool call (agent, tool, arguments, duration). Useful in the CLI, where you don't have Tempo open.

Wiring order (`OnCallAssistant.Create`): `UseOpenTelemetry` first, so it's the outermost layer and sees every run,
including runs another layer might stop. Then `Use(toolMiddleware.InvokeAsync)`.

## When NOT to use it

- **Not for logic the tool itself owns.** Clamping the time window and validating the trace id live in the tools
  (`TelemetryTools`), because they are rules about that tool's arguments. Middleware is for rules about *all* tools
  or the run as a whole.
- **Not as the approval gate for actions that change the system.** You could refuse a `RestartService` call in
  function middleware, but MAF has a built-in for that: `ApprovalRequiredAIFunction` turns the call into a
  `ToolApprovalRequestContent` the caller must answer (sample Example 4). In a workflow, use `RequestPort` (Phase 3).
  An approval written as middleware can't be seen in the tool definition and is easy to lose when the pipeline is rebuilt.
- **Not for telemetry you get for free.** `UseOpenTelemetry` already emits `invoke_agent`, `chat` and `execute_tool`
  spans with token counts and (with sensitive data on) arguments and results. The middleware's log lines duplicate
  part of that on purpose, for the console. Don't grow them into a second tracing system.
- **Not for limits the loop already has, if they're enough.** `FunctionInvokingChatClient` has its own
  `MaximumIterationsPerRequest`. The custom budget exists because it's configurable per host (`Budget` section), it
  answers the model with a clear message, and the eval runner can count the hits (`budget_hits`). How the built-in
  cap ends a run when it's reached isn't verified here.

## Gotchas

- **`Iteration` is 0-based in practice.** With M.E.AI 10.11.0, the tools of the model's first response see
  `Iteration == 0`, although the docs describe the first request as iteration 1. Hence the `+ 1`. Verified by tests
  (`Runs_tools_within_the_budget_and_refuses_them_after_it`), not by docs.
- **Parallel calls share one `Iteration`.** Three tools requested in one response are one round trip, allowed or refused
  together (`Parallel_calls_in_one_model_response_count_as_one_round_trip`). The budget counts round trips, not tool
  calls, on purpose: the cost is in re-sending the conversation, not in the number of calls.
- **No state needed for a per-run budget.** `Iteration` starts again on every run, so the same middleware instance
  works across CLI turns (`The_budget_starts_again_on_every_run`). A budget across a whole *session* would need state,
  which raises the question of where to keep it (the session, not the middleware).
- **Function middleware needs a `ChatClientAgent` underneath.** It hooks into that agent's function-calling loop. On
  another `AIAgent` (a remote agent, a workflow as agent) it has nothing to attach to.
- **A refusal is still a tool result.** The model sees the budget message where the data would be. The eval runner
  can't find it in `ChatMessage.Text` (that only joins `TextContent`). It has to look in the `FunctionResultContent`s
  (`RunMetrics`).
- **The result is already JSON when middleware sees it.** For `AIFunctionFactory` functions, `next` returns a
  `JsonElement` (indented), not the C# record. Measure that, not a fresh serialization of the record, or the size
  won't match what the model gets (`Logs_the_size_of_each_result_as_the_model_receives_it`).
- **Order matters.** Each `Use` wraps the previous chain, so the first `Use` is the outermost layer. That's why
  `UseOpenTelemetry` goes first: a layer outside it could stop a run before telemetry sees it.

## Numbers

- Phase 1 baseline (`evals/results/phase-1.md`): the budget of 10 was never hit. Max 4 round trips, 0 budget hits, 0
  failed tool calls in 6 runs. Right now it's a safety net, not a constraint.
- Logged result sizes: 160–1,960 characters (~40–490 tokens), well under the 4,000-character warning.
- Not measured yet: a run with a tight budget (e.g. 2) against the real model, to see whether it answers well from
  partial evidence or just gets worse. That's the "break it on purpose" experiment for this concept.
