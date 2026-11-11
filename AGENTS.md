# AGENTS.md — maf-incident-responder

Context for coding agents (Claude Code, OpenCode, Copilot, Kiro, Codex...). Read this whole file at the start of every
session, then `docs/PLAN.md` to see the current phase and its open tasks.

---

## 1. What this repo is (and is not)

**A learning-in-public project.** The goal is to learn **Microsoft Agent Framework (MAF)** in depth by building
one system that grows phase by phase: an **Incident Responder** that investigates
production symptoms (logs, metrics, traces) of a small lab app, finds the root cause, proposes a remediation,
waits for human approval, and writes the postmortem.

- **Goal:** understand MAF well enough to apply it in real-world projects. The product is secondary.
- **Public:** this is a public GitHub repo and feeds a blog series (one post per phase). Code and docs must be
  presentable and honest about trade-offs.
- **Not a goal:** a production-grade SRE tool, supporting every telemetry backend, or perfect UI.

Success = at the end of each phase the author can explain *why* each MAF concept used exists, when he would NOT
use it, and has numbers (evals) showing what changed.

## 2. How agents should work in this repo

The point of the repo is that a human learns MAF, so agents work as pair programmers, not as autopilots:

- Before introducing a MAF concept for the first time, point to the sample in the local clone that shows it
  (§4) and explain it in a few lines: what problem it solves, how it works, the main gotcha.
- Propose the design first; implement after agreement. Plumbing that is not MAF (HTTP clients for Loki /
  Prometheus / Tempo, Aspire wiring, JSON parsing) can be implemented directly.
- If you are not sure about a MAF API, say so and check the clone. Do not guess.
- After a concept is integrated, suggest a short note in `docs/notes/` (template there).
- Don't expand scope silently. Work that belongs to a later phase gets a `TODO(Fn)`.
- Language: code, comments, commit messages and docs are in English.

Personal preferences of whoever is driving the agent live outside the repo (user-level agent config or a
git-ignored local file), not here.

## 3. Repository layout

```
maf-incident-responder/
├─ AGENTS.md                 ← this file (CLAUDE.md imports it; OpenCode reads it natively)
├─ opencode.json             ← OpenCode config: also loads docs/PLAN.md as instructions
├─ README.md                 ← public face of the repo
├─ IncidentResponder.slnx    ← solution (new XML format)
├─ Directory.Build.props     ← net10.0, nullable, implicit usings, analyzers
├─ Directory.Packages.props  ← Central Package Management: ALL versions live here
├─ global.json               ← SDK 10.0.1xx + Microsoft.Testing.Platform runner
├─ docs/
│  ├─ PLAN.md                ← phases, tasks (checkboxes), MAF samples per phase  ← source of truth for progress
│  ├─ ARCHITECTURE.md        ← components, flows, how it evolves per phase
│  ├─ adr/                   ← architecture decision records
│  └─ notes/                 ← one note per MAF concept learned (blog raw material)
├─ src/
│  ├─ Lab/                   ← Phase 0: the "victim" system (no MAF here)
│  │  ├─ IncidentLab.AppHost         Aspire orchestration + grafana/otel-lgtm + Postgres + Redis
│  │  ├─ IncidentLab.ServiceDefaults OTel, health, service discovery, resilience
│  │  ├─ IncidentLab.Platform        fault injection ("chaos") middleware + /_ops/runtime control endpoints
│  │  ├─ IncidentLab.CatalogApi      minimal API
│  │  ├─ IncidentLab.OrdersApi       minimal API, calls catalog-api
│  │  └─ IncidentLab.LoadGen         background traffic (no traffic = no incident)
│  └─ Responder/             ← Phases 1+: the agent system
│     ├─ IncidentResponder.Core       domain records + ITelemetryQuery. NO MAF / no AI deps.
│     ├─ IncidentResponder.Telemetry  ITelemetryQuery over Loki/Prometheus/Tempo. NO MAF.
│     ├─ IncidentResponder.Agents     all MAF code: chat client factory, tools, middleware, agents
│     └─ IncidentResponder.Cli        Phase 1 entry point (interactive on-call assistant)
├─ tests/IncidentResponder.Tests     ← deterministic tests (FakeChatClient). Runs in CI.
├─ evals/                    ← scenarios with known root cause + runner (Phase 1) + results
├─ spikes/                   ← isolated experiments reproducing a MAF sample before integrating it
├─ blog/                     ← post drafts, one per phase
└─ infra/                    ← Bicep/azd for Foundry resources (Phase 4)
```

Projects added in later phases (create them when the phase starts, not before):

| Phase | Project | Purpose |
|---|---|---|
| 1 | `evals/IncidentResponder.Evals` | eval runner console app |
| 2 | `src/Responder/IncidentResponder.McpServer` | telemetry tools exposed over MCP |
| 3 | `src/Responder/IncidentResponder.Workflows` | the investigation workflow (executors, edges, HITL) |
| 4 | `src/Responder/IncidentResponder.Host` | ASP.NET host: AG-UI endpoint, auth, durable |
| 4 | `src/Responder/IncidentResponder.WarRoom` | frontend (AG-UI client) |
| 5 | `src/Responder/IncidentResponder.Postmortem` | Harness agent that writes the postmortem |

## 4. The local MAF clone is the reference — do not trust memory

MAF moves fast and its API changes between versions. **Before using any MAF API, look at how the samples and
source in the local clone use it.**

- Local clone: clone `https://github.com/microsoft/agent-framework` **next to this repo**, so it is at
  `../agent-framework`.
- .NET samples: `../agent-framework/dotnet/samples/` (`01-get-started`, `02-agents`, `03-workflows`,
  `04-hosting`, `05-end-to-end`)
- .NET source: `../agent-framework/dotnet/src/`
- Samples reference MAF via `ProjectReference`; this repo uses NuGet packages. If an API from the clone is
  missing in the pinned package, the clone is ahead of NuGet: say so, don't hack around it.
- `docs/PLAN.md` lists the exact samples to study for each phase.

Key facts verified in the clone (Nov 2026, VersionPrefix 1.28.0):
- `IChatClient.AsAIAgent(instructions, name, description, tools)` → `ChatClientAgent`
  (`src/Microsoft.Agents.AI/ChatClient/ChatClientExtensions.cs`).
- Sessions: `AgentSession session = await agent.CreateSessionAsync(); await agent.RunAsync(msg, session);`
- Structured output: `agent.RunAsync<T>(...)` or `ChatResponseFormat.ForJsonSchema<T>()`
  (`02-agents/Agents/Agent_Step02_StructuredOutput`).
- Function-call middleware: `agent.AsBuilder().Use(FuncMiddleware).Build()` with signature
  `(AIAgent, FunctionInvocationContext, Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>, CancellationToken)`
  (`02-agents/Agents/Agent_Step11_Middleware`).
- Foundry → IChatClient: `new AIProjectClient(uri, cred).GetProjectOpenAIClient().GetResponsesClient().AsIChatClient(deployment)`.
- Workflows: `WorkflowBuilder`, `Executor<TIn,TOut>`, `AddEdge`, `AddFanOutEdge`, `AddFanInBarrierEdge`,
  `RequestPort.Create<TReq,TRes>`, `RequestInfoEvent` + `SendResponseAsync(ExternalResponse)`,
  checkpoints via `SuperStepCompletedEvent` → `CheckpointInfo` and `RestoreCheckpointAsync`.
- Magentic: `new MagenticWorkflowBuilder(manager).AddParticipants(...).RequirePlanSignoff(..).WithMaxRounds(..).WithMaxStalls(..).WithMaxResets(..)`.
- Durable agents/workflows moved to a separate repo (`microsoft/agent-framework-durable-extension`);
  the folders in `04-hosting/Durable*` only contain a README pointing there.

## 5. Pinned versions

See `Directory.Packages.props`. Verified against nuget.org on 2026-11-11 (clean `dotnet restore --force`).
Policy: MAF = latest stable; packages MAF depends on = the version MAF is built against (not the newest);
everything else = latest stable patch.

| Package | Version | Verified | Note |
|---|---|---|---|
| Microsoft.Agents.AI | 1.28.0 | ☑ | latest stable; matches the local clone's VersionPrefix |
| Aspire | 13.6.5 | ☑ | latest patch (clone uses 13.6.4) |
| Microsoft.Extensions.AI* | 10.11.0 | ☑ | what MAF 1.28.0 depends on; 11.0.0 exists, not used |
| OpenAI | 2.14.0 | ☑ | what M.E.AI.OpenAI 10.11.0 depends on; 2.15.0 exists |
| Azure.AI.Projects / Azure.AI.Extensions.OpenAI | 3.0.0-beta.5 | ☑ | same as the MAF clone; beta.6 exists |
| Microsoft.Extensions.Hosting / Http | 10.0.15 | ☑ | 10.0.x servicing line; Aspire 13.6.5 needs ≥ 10.0.14 |
| OpenTelemetry (core / instrumentation) | 1.21.0 / 1.20.x | ☑ | released separately |
| xunit.v3.mtp-v2 | 4.0.3 | ☑ | latest patch |
| `grafana/otel-lgtm` image | 0.40.0 | ☑ | pinned in `AppHost.cs`, not in props |

Never float versions. Upgrading MAF is a deliberate, separate commit (`chore(deps): bump MAF to x.y.z`) followed
by a full test + eval run — and it is blog material ("what changed between versions").

## 6. Architecture principles (non-negotiable)

1. **LLM where judgment is needed, deterministic code everywhere else.** Parsing, aggregation, scoring, routing
   on known conditions: code. Interpreting evidence and planning an investigation: model.
2. **Core has no AI dependencies.** `IncidentResponder.Core` and `.Telemetry` never reference MAF or
   Microsoft.Extensions.AI. All MAF code lives in `.Agents` (and later `.Workflows`, `.Host`, ...).
3. **The provider is behind `IChatClient`.** Only `ChatClientFactory` knows about Foundry. Tests use
   `FakeChatClient`.
4. **Tool design.**
   - Every tool is bounded: required time window (max 120 min), top-N results.
   - Outputs are **summaries** (grouped, counted, downsampled). Never raw dumps: they blow the context window.
   - Descriptions are written for the model: what it returns and when to use it.
   - **Read-only tools are free. Any tool that changes the system (restart, rollback, scale) requires approval**
     (`ApprovalRequiredAIFunction` at tool level and/or `RequestPort` at workflow level). No exceptions.
5. **The chaos must not leak the answer.**
   - Injected errors read like real production errors; never contain "chaos", "fault", "injected", "simulated".
   - The same goes for **code names that reach telemetry**: namespaces, type and file names appear in stack traces,
     routes appear in request logs. That is why the fault injection library is `IncidentLab.Platform`
     (`RequestPipelineMiddleware`, `/_ops/runtime`) and not "Chaos".
   - `/_ops`, `/health`, `/alive` are filtered out of traces, and framework request logs are at Warning
     (`ServiceDefaults`).
   - Responder tools must never call `/_ops/runtime`. Only the eval runner does.
6. **Evals ground truth comes from the injected scenario, never from a model.**
7. **One kind of change per commit.** Don't mix a refactor, a prompt change and a new feature: otherwise eval
   deltas can't be attributed.
8. **Prompts are code.** They live in source (e.g. `OnCallAssistant.Instructions`), are reviewed in diffs, and
   every change is followed by an eval run.

## 7. Phases (summary — details and checklists in `docs/PLAN.md`)

One evolving system. Each phase ends with: git tag `phase-N`, eval run + `evals/results/phase-N.md`,
blog draft in `blog/`.

| # | Phase | Weeks* | Adds | MAF concepts |
|---|---|---|---|---|
| 0 | Lab | 1 | Aspire app, chaos, LGTM, 3 scenarios with known cause. *No MAF. Time-boxed.* | — |
| 1 | On-call assistant | 2–3 | CLI agent with telemetry tools, middleware, sessions, structured `Diagnosis`, eval runner | `ChatClientAgent`, tools, `AgentSession`, structured output, middleware, OTel |
| 2 | Knowledge | 4 | Tools behind an MCP server, RAG over runbooks, memory of past incidents | MCP, RAG (`TextSearchProvider`/vector store), `AIContextProvider`, memory |
| 3 | Workflow | 5–7 | Triage → fan-out → aggregate → HITL → execute → verify, with checkpoints. Then a Magentic variant and a comparison | Workflows, executors, edges, fan-out/fan-in, `RequestPort`, checkpoints, agents-as-executors, Magentic |
| 4 | Productization | 8–9 | ASP.NET host, AG-UI war room, durable escalation timer, auth, OTel of the agent, Foundry evals, A2A | AG-UI, durable, hosting, evaluation, A2A, Foundry hosted agents |
| 5 | Postmortem | 10–11 | Harness agent that writes the postmortem, computes impact, loops until it meets a rubric | Harness, `TodoProvider`, `FileAccessProvider`, CodeAct, `LoopAgent` |

\* ~15 h/week.

**Current phase:** 0 — see `docs/PLAN.md` for the next unchecked task.

## 8. Commands

```bash
# build & test (no Azure needed)
dotnet build IncidentResponder.slnx
dotnet test --solution IncidentResponder.slnx

# run the lab (needs Docker Desktop running): Aspire dashboard + Grafana on http://localhost:3000
dotnet run --project src/Lab/IncidentLab.AppHost

# inject / clear a fault (fixed ports from launchSettings.json: catalog-api 65448, orders-api 65447)
curl -X POST http://localhost:65448/_ops/runtime -H "Content-Type: application/json" \
     -d '{"kind":"Errors","probability":0.6,"errorMessage":"..."}'
curl -X DELETE http://localhost:65448/_ops/runtime

# Phase 1 CLI (needs `az login` and a Foundry project with a model deployment)
cd src/Responder/IncidentResponder.Cli
dotnet user-secrets set "Foundry:ProjectEndpoint" "https://<resource>.services.ai.azure.com/api/projects/<project>"
dotnet user-secrets set "Foundry:Model" "gpt-5.4-mini"
dotnet run
```

## 9. Testing strategy

- **Unit / deterministic (CI):** `FakeChatClient` scripts the model (tool call → final answer); assert on which
  tools ran, with what arguments, and what the agent returned. `StubTelemetryQuery` for telemetry.
  HTTP clients tested with a fake `HttpMessageHandler` and recorded JSON payloads from the real LGTM.
- **Evals (manual, real model):** `evals/`. Not in CI (cost, non-determinism). Run at the end of each phase and
  after any prompt/model/MAF-version change.
- Test names describe behaviour: `Calls_the_error_logs_tool_the_model_asks_for_and_returns_the_final_answer`.

## 10. Conventions

- .NET 10, C# latest, nullable enabled, file-scoped namespaces, primary constructors where they help.
- No `Version` in `.csproj` — only in `Directory.Packages.props`.
- `TimeProvider` injected wherever time matters (testability).
- `CancellationToken` on every async public method; tools accept it (MAF passes it through).
- Commits: Conventional Commits (`feat(agents): ...`, `fix(lab): ...`, `docs: ...`, `chore(deps): ...`).
- Branch per phase task is optional; tag per phase is mandatory: `git tag phase-1`.
- `TODO(Fn)` marks work that belongs to phase n. Search for them when a phase starts.

## 11. Secrets, cost and safety

- No secrets in the repo, ever. Use user-secrets and `DefaultAzureCredential` (`az login`). The repo is public.
- Default to a mini model. Set an Azure budget alert. Tear down Foundry extras (hosted agents, Bing grounding)
  after each phase (`azd down` once infra exists).
- The eval runner must print token usage and estimated cost per run.
- Remediation actions in the lab only affect the lab (restart a container, clear a fault). Never point the
  responder at anything real.

## 12. Open verification items (Phase 0)

Remove this section once Phase 0 is done.
- `LgtmTelemetryQuery` is a stub (`NotImplementedException`) — implemented in Phase 1.
