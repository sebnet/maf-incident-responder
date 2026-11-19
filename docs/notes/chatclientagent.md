# ChatClientAgent — notes

- Phase: 1
- Samples studied: `../agent-framework/dotnet/samples/01-get-started/01_hello_agent`, `02_add_tools`, `03_multi_turn`
- MAF version: 1.28.0

## The problem it solves

An `IChatClient` only sends messages to a model and returns its answer. Turning that into an agent means building
the rest yourself: keeping the instructions with every call, describing the tools to the model, running the tools
the model asks for and sending their results back (again and again until it answers), keeping the conversation
history, and offering one entry point for all of it. `ChatClientAgent` is that layer.

## How it works

`AIAgent` is MAF's abstraction of an agent: `RunAsync` / `RunStreamingAsync` (and `RunAsync<T>` for structured
output), `CreateSessionAsync`, a name and an id. `ChatClientAgent` is one implementation of it, the one that connects
agent behaviour to an `IChatClient`. Other `AIAgent`s are wrappers (telemetry, middleware), remote agents, and
workflows exposed as agents.

One way to build it is the `AsAIAgent` extension on an `IChatClient`:

```csharp
// OnCallAssistant.Create
return chatClient
    .AsAIAgent(instructions: Instructions, name: Name, description: "...", tools: tools.AsAITools()) // ChatClientAgent
    .AsBuilder()
    .UseOpenTelemetry(TelemetrySourceName, configure: agent => agent.EnableSensitiveData = sensitiveTelemetry)
    .Use(toolMiddleware.InvokeAsync)
    .Build();                                                                                        // AIAgent
```

- **A run is not one round trip.** `RunAsync` sends the instructions, the session's history, the new message and the
  tool descriptions to the model. If the model asks for tools, the agent runs them and calls the model again with the
  results, until the model answers. One run is usually several model round trips (3–4 in the Phase 1 baseline).
- **Tools.** The tools are passed to the agent. When the model requests one, the function-calling loop the agent puts
  on top of the `IChatClient` (`FunctionInvokingChatClient`) runs it. The model can call any tool, any number of times,
  and ask for several in one response.
- **Middleware** wraps the agent rather than living inside `ChatClientAgent`, and each kind runs at a different
  moment: agent-run middleware around the whole run, function middleware around each tool call (this repo's
  `ToolCallMiddleware`), chat-client middleware around each model call. Uses: logging, changing input or output,
  short-circuiting (the round-trip budget refuses a tool instead of running it).
- **Sessions.** If you pass an `AgentSession`, the run's input messages and the responses are added to its history,
  and the next run sees them (the CLI's chat). Without one, a run starts with no history: `DiagnoseAsync` creates a
  fresh session per diagnosis so runs don't influence each other.

## When NOT to use it

With an agent, **the model decides the steps**: which tools, in what order, how many times, when it has enough. That
is its strength and its price. The same s01 took 3 round trips once and 4 another time, with different tools, so
cost and time vary, and it can be wrong (it once cited a source it never queried). Pay that price when you can't know
the steps in advance, like investigating an unknown incident. Don't pay it when you already know them (AGENTS.md
principle 1: LLM where judgment is needed, deterministic code everywhere else).

- **The steps are known: plain code.** The eval runner's scoring (compare fields with the expected answer) and the
  rewrite of Polly's log lines are code: always the same result, no tokens, no surprises.
- **The sequence is known: a workflow, with agents inside the steps that need judgment.** Phase 3's process (triage
  → parallel investigation → aggregate → human approval → execute → verify) is known, so the workflow (code) fixes
  the order, the parallelism and the approval gate. Inside it, each investigator is still a `ChatClientAgent` with a
  narrow set of tools that decides how to investigate its hypothesis. Given the whole process, one agent could skip
  the approval step: that can't be left to the model.
- **The agent's definition lives in the provider** (Foundry-managed "prompt" agents: instructions and tools configured
  in Foundry): retrieve it by name as an `AIAgent` instead of building one on a chat client (sample
  `04-hosting/FoundryHostedAgents/responses/Hosted-FoundryAgent`). Not this project's choice: here prompts are code,
  versioned and evaluated (AGENTS.md principle 8). Don't confuse it with Foundry *hosted* agents, where Foundry runs
  *your* agent code, which can be a `ChatClientAgent` (Phase 4, optional).

## Gotchas

- `AsAIAgent` adds its own function-calling loop on top of your client, unless the client already contains one.
  Anything you wrap around the client before `AsAIAgent` ends up *below* that loop (one call per round trip).
- After `.AsBuilder()...Build()` you hold an `AIAgent` chain (telemetry agent → middleware agent →
  `ChatClientAgent`), no longer the `ChatClientAgent` itself. That is why `Create` returns `AIAgent`.
- Function middleware only works with a `ChatClientAgent` underneath: it hooks into the function-calling loop, which
  lives there.
- Tools the model requests in one response run one after another by default (`AllowConcurrentInvocation` is off),
  verified in a Tempo trace.
- One run's `AgentResponse` holds the whole run: `Usage` summed over all round trips, and `Messages` with every tool
  call and tool result (what the eval runner counts).

## Numbers

Phase 1 baseline (gpt-4.1-mini, s01 and s02 × 3): 6/6 correct, 3–4 round trips and 3–4 tool calls per diagnosis,
about 3,800–5,600 input and ~330 output tokens, ~$0.002–0.003 and 10–13 s each (`evals/results/phase-1.md`).
