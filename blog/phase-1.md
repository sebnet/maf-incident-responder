---
title: "An on-call assistant with Microsoft Agent Framework: tools that don't drown the model"
subtitle: "Phase 1: one agent, three read-only tools, a structured diagnosis, and the first eval numbers"
tags: dotnet, ai, agents, microsoft-agent-framework, opentelemetry
---

<!-- DRAFT. Code at tag `phase-1`. Numbers from evals/results/phase-1.md. -->

In the [introduction](https://incident-responder-1.hashnode.dev/i-m-learning-microsoft-agent-framework-by-building-an-on-call-agent-in-public)
I promised numbers instead of vibes. This is the first phase with numbers.

The goal of Phase 1 was small on purpose: **one agent, in a terminal, that answers "what's wrong with orders-api?"
with a structured diagnosis backed by evidence**, and an eval runner that scores it against incidents whose root cause
I know because I injected them.

The result: the agent got two of the three scenarios right every time, at about a quarter of a cent per diagnosis. The
third one taught me more than the first two. It found the right problem every time and still scored zero. I'll get to that.

## The agent is the easy part

In Microsoft Agent Framework (MAF), the agent itself is a few lines:

```csharp
return chatClient
    .AsAIAgent(instructions: Instructions, name: "OnCallAssistant",
               description: "Investigates production symptoms using read-only logs, metrics and traces.",
               tools: tools.AsAITools())
    .AsBuilder()
    .UseOpenTelemetry(TelemetrySourceName, configure: a => a.EnableSensitiveData = sensitiveTelemetry)
    .Use(toolMiddleware.InvokeAsync)
    .Build();
```

`AsAIAgent` turns any `IChatClient` (here, a Foundry deployment of `gpt-4.1-mini`) into a `ChatClientAgent`. You give
it instructions and tools, and it runs the loop: send the conversation, run the tools the model asks for, send the
results back, repeat until the model answers. One `RunAsync` is **not one model call**. In this phase a diagnosis
took 3–4 round trips.

That loop is the whole value of an agent and also its price: **the model decides the steps.** Same incident, same
prompt: one run took 3 round trips, another took 4 with a different tool. When you already know the steps, you
don't want an agent. You want code, or a workflow with agents only in the steps that need judgment. That's Phase 3.
Investigating an incident you've never seen is exactly where you don't know the steps.

The interesting work was everywhere around those few lines.

## Tools: the model only sees what you give it

The model can't open Grafana. Everything it knows about the incident comes from three tools:

- `get_error_logs(service, minutes)`: *what* is failing
- `get_metric(service, metric, minutes)`: *when* it started and *how bad* it is
- `get_trace(traceId)`: *where* in the call chain it fails

Raw telemetry is the worst possible input for a model: thousands of log lines, a sample per second, traces with
dozens of spans. And a tool's output is the model's next input, re-sent on **every later round trip** of the run. So
each tool does in code what code does better than a model:

- **Logs** are grouped by message template, exception and request, ids are normalized (`/items/3` → `/items/{n}`),
  counted, top-N, one sample trace id per group.
- **Metrics** become min/max/avg/last plus at most 30 points.
- **Traces** become total duration, the 5 slowest spans and the error spans.
- **Everything is bounded:** a time window of at most 120 minutes, whatever the model asks for.

A full investigation reads 3–4 tool results of 160–1,960 characters each. The whole diagnosis, prompt and tool
definitions included, is ~4,000–5,600 input tokens.

### The circuit breaker that wasn't (or was)

The lesson I liked most came from a single log line. orders-api calls catalog-api through .NET's standard resilience
handler (Polly), and when retries run out it logs something like:

```
Execution attempt. Source: '-standard//Standard-Retry', Operation Key: '', Result: '500', Handled: 'True', Attempt: '3', ...
```

With that line in the tool output, the model reported "the circuit breaker opened" in 2 of 2 runs. I hadn't seen the
breaker open, so I called it a hallucination and rewrote the line in plain words from its structured fields:

```
outgoing call failed, retries exhausted: GET https+http://catalog-api/items/{n} -> 500 after 4 attempts
```

The breaker claims stopped. Then, looking at Loki for something else, I found that the circuit breaker *does* open
intermittently during that scenario. The only baseline run that mentioned it was the only one whose time window
contained an `OnCircuitOpened` event.

Two lessons. **Unclear output gets "completed" by the model**, so write tool output in plain words. And **check the
data before calling something a hallucination.**

## Middleware: rules that hold however the model behaves

MAF has middleware at three levels: around the whole run, around each model call, and around each tool call. I used
one, around tool calls, for two things:

1. **A round-trip budget.** `FunctionInvocationContext.Iteration` tells you which round trip of the run you're in.
   Past the limit, the tool doesn't run. The model gets a tool result saying "budget exhausted, answer with what you
   have", so the run still ends with a diagnosis instead of an exception.
2. **Result sizes.** Every tool call logs how many characters the model will read, and warns above 4,000. It never
   cuts: keeping results small is the tool's job, and the middleware just makes it visible.

Cost per run is round trips × what each one re-sends. The budget bounds the first, the size log shows the second.

One gotcha: with Microsoft.Extensions.AI 10.11.0, `Iteration` is 0-based for the first round trip, although the docs
describe the first request as iteration 1. I only know that because a test with a scripted fake model failed.

Honest number: in this phase the budget (10) was **never hit**. Maximum 4 round trips. It's a safety net for now.

## Structured output: the answer is a C# type

To score a diagnosis, I can't parse "the problem seems to be in the catalog service". So the final answer is a
`Diagnosis`:

```csharp
public sealed record Diagnosis(
    [property: Description("Service where the root cause is, which may differ from the alerting service")] string RootCauseService,
    [property: Description("What is wrong in the root-cause service. ApplicationError: ... DatabaseConnectivity: ...")] RootCauseCategory Category,
    [property: Description("Root cause in one sentence")] string RootCause,
    [property: Description("0..1")] double Confidence,
    IReadOnlyList<Evidence> Evidence,
    IReadOnlyList<string> SuggestedActions);
```

`agent.RunAsync<Diagnosis>(...)` builds a JSON schema from the type, sends it to the model, and deserializes
the final answer. Tools keep working during the run. Only the last answer has to fit the schema.

The `[Description]`s travel in the schema, so **they're part of the prompt.** Remember that, because it comes back in
the s03 story.

## Seeing the agent think: OpenTelemetry

`UseOpenTelemetry` gives each run a trace with three layers: `invoke_agent` for the whole run, one `chat` span per
model round trip, one `execute_tool` span per tool. I send them to the same Grafana LGTM stack as the lab, so the
agent's traces sit next to the traces of the system it's investigating.

<!-- IMAGE: Tempo trace of one s01 diagnosis: invoke_agent → chat / execute_tool spans -->

Small surprise: the system instructions are **not** on the `invoke_agent` span. The agent adds them later, when it
calls the model, so you find them (and the schema) on the `chat` spans.

## The eval runner

This is the part that makes the rest honest. Per scenario and repetition, the runner:

1. clears every fault and lets the lab settle for 5 minutes,
2. injects the fault,
3. lets traffic flow for a warmup period,
4. builds a fresh agent whose tools can't look further back than 5 minutes before the injection (so a run never sees
   the previous one's telemetry),
5. asks for a diagnosis with nothing but the alert,
6. clears the fault, scores the answer, and records tokens, cost, round trips, tools and latency.

The ground truth (root cause service, category, terms the answer must mention) is written by me, from the fault I
injected. Never by a model.

## The numbers

`gpt-4.1-mini`, 3 runs per scenario:

| Scenario | What I broke | Service | Category | All correct | Tokens in / out (avg) | Cost per diagnosis | Latency |
|---|---|---|---|---|---|---|---|
| s01 | catalog-api fails on corrupt data; orders-api alerts | 3/3 | 3/3 | 3/3 | 5,649 / 344 | ~$0.003 | 12.2 s |
| s02 | orders-api is slow, no errors | 3/3 | 3/3 | 3/3 | 3,828 / 314 | ~$0.002 | 10.8 s |
| s03 | a background job holds every Postgres connection | 3/3 | **0/3** | **0/3** | 4,160 / 309 | ~$0.002 | 11.5 s |

**s01** is the one I cared about most: the alert fires on orders-api, but the culprit is catalog-api. In every run the
agent followed the dependency, opened a sample trace, found the `InvalidOperationException` inside catalog-api, and
never blamed the service that alerted.

**s02** was clean: orders-api p95 up, no error logs, catalog-api's latency flat. Conclusion: "the delay is inside
orders-api". It ruled out the dependency with data rather than assuming it.

### s03: right answer, wrong label

In s03 a "background export job" in orders-api grabs all 20 connections of the Postgres pool, and normal requests fail
with Npgsql's real error: "The connection pool has been exhausted".

All three runs found exactly that: orders-api, connection pool exhausted, `NpgsqlException`, max pool size 20, with
confidence 1. And all three labelled it `ResourceSaturation` instead of `DatabaseConnectivity`. Score: 0/3.

The reason is the schema. The category definitions the model reads live on the `Category` property:

> DatabaseConnectivity: cannot get or use database connections. [...] ResourceSaturation: CPU, memory, threads or
> queues **exhausted**.

The `DatabaseConnectivity` definition I wrote in the enum *does* say "pool exhausted". But descriptions on enum values
**don't reach the JSON schema**: the schema lists only the names. I knew that. I had a test for it and had copied the
definitions onto the property for exactly this reason, but the copy on the property didn't say "pool". The model saw "exhausted" under one category and nothing about pools
under the other, and picked the obvious one.

Is the model wrong? A connection pool *is* a saturated resource. The taxonomy was ambiguous, and the model resolved it
differently from me. A schema guarantees the shape of the answer, not that you and the model agree on what the words
mean.

The fix was one sentence on the property: an exhausted connection pool is `DatabaseConnectivity`, and pools are
not `ResourceSaturation`. It's a prompt change, so all three scenarios ran again:

| Scenario | Before | After |
|---|---|---|
| s01 | 3/3 | 3/3 |
| s02 | 3/3 | 3/3 |
| s03 | 0/3 | **3/3** |

Same tools, same investigation, same cost (~$0.002 per diagnosis). Only the words in the schema changed. That
might be the most useful number of the phase: **a wording change in the output type moved one scenario from 0% to
100%.**

### What no run found

No run found *who* was holding the connections. The export job announces itself in an Information log, and the agent
only reads error logs. Its 20-second queries are in spans the agent never opened. Two of three runs suggested
"increase Max Pool Size" as the first action. The team's runbook for this incident says, in bold, **don't**: it hides
the problem and moves the pressure to Postgres.

That's the setup for Phase 2. The agent gets the runbooks. Does that stop the bad advice? Does it send the agent
looking for the job?

## What I'd tell myself at the start of Phase 1

- **Tool output is prompt.** Design it as carefully as the instructions, and in plain words.
- **Descriptions on your output type are prompt too**, and only the ones that reach the schema count.
- **Score the evidence, not only the answer.** In one s01 run, the evidence cited catalog-api's *logs*, a tool it
  never called. The fact was true (it came from a trace), the source was invented, and my field-based scorer couldn't
  see it.
- **Three runs is a small sample.** 6/6 on two clean, single-cause synthetic faults says the agent handles these
  cases, not that it's accurate. Real incidents are messier.

Next: **Giving the agent a team memory**: runbooks through RAG, a memory of past incidents, and the telemetry tools
moved behind an MCP server. With s03 as the first test of whether knowledge helps.

The code for this post is at the [`phase-1` tag](https://github.com/sebnet/maf-incident-responder/tree/phase-1).
