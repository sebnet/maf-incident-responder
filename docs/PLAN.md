# Plan — phases, tasks and samples

Source of truth for progress. Tick boxes as you go. Each phase has: goal, MAF concepts, samples to study
(paths relative to `../agent-framework/dotnet/samples/`), tasks, exit criteria and the blog post.

Routine for every new MAF concept:
1. Run the sample **unchanged** (you have Foundry, they work as-is).
2. Break it on purpose (remove an approval, change an edge) and watch what happens.
3. Optional: reproduce it in `spikes/<concept>/`.
4. Integrate it into the responder.
5. Write `docs/notes/<concept>.md` (problem it solves, how it works, when NOT to use it, gotchas).

Closing routine for every phase:
- [ ] All tests green, no new warnings.
- [ ] Eval run (≥3 repetitions per ready scenario) → `evals/results/phase-N.md`.
- [ ] `git tag phase-N` and push.
- [ ] Blog draft in `blog/phase-N.md`.
- [ ] Update "Current phase" in `AGENTS.md`.

---

## Phase 0 — Lab (week 1, time-boxed: ~15 h)

**Goal:** a small system that fails on demand in realistic ways, with telemetry you can query.
**No MAF in this phase.** If it takes more than a week, cut scope, don't extend it.

Tasks
- [x] Verify package versions against nuget.org; fix `Directory.Packages.props`; fill the table in `AGENTS.md` §5.
- [x] `dotnet build` + `dotnet test` green on your machine.
- [x] Move `docs/setup/ci.yml` to `.github/workflows/ci.yml` (kept outside `.github/` during the initial setup), `git init`, first commit, push, and check CI is green.
- [x] Run the AppHost; confirm all resources start (Docker Desktop must be running).
- [x] Confirm telemetry reaches LGTM: open Grafana (:3000) → Explore → see logs (Loki), metrics (Prometheus),
      traces (Tempo) for `orders-api` and `catalog-api`.
- [x] Verify the Loki/Prometheus/Tempo HTTP APIs are reachable from the host (ports 3100/9090/3200), or switch
      to Grafana's datasource proxy. Record which labels exist (`service_name`? `detected_level`?) in
      `docs/notes/lgtm-labels.md`.
- [x] Inject s01 by hand (`POST /chaos` on catalog-api): see the orders-api error rate rise in Grafana and the
      500s in Tempo traces crossing both services.
- [x] Inject s02 by hand: see p95 latency rise with no errors.
- [ ] Move orders-api to Postgres (Aspire.Npgsql + `ordersdb`) so s03 can become a real connection-pool problem.
      *(Optional within the time box; otherwise carry it to Phase 2.)*
- [ ] Write one runbook per scenario in `docs/runbooks/` (used for RAG in Phase 2). Short, the way an SRE team
      would actually write them.

Exit criteria
- Two scenarios can be injected and are clearly visible in Grafana. Telemetry APIs answer from the host.

Blog: *"Building a lab that breaks on purpose: Aspire + OpenTelemetry + Grafana LGTM"* (optional, short).

---

## Phase 1 — On-call assistant (weeks 2–3)

**Goal:** a single agent, in a CLI, that answers "what's wrong with orders-api?" with a structured diagnosis
backed by evidence. Plus the eval runner, so every later phase has a baseline to beat.

MAF concepts: `ChatClientAgent`, function tools, `AgentSession`, structured output, function-call middleware,
OpenTelemetry for the agent, chat reduction/compaction (awareness).

Samples
- `01-get-started/01_hello_agent`, `02_add_tools`, `03_multi_turn`, `04_memory`
- `02-agents/Agents/Agent_Step02_StructuredOutput`
- `02-agents/Agents/Agent_Step03_PersistedConversations`
- `02-agents/Agents/Agent_Step05_Observability`, `02-agents/AgentOpenTelemetry`
- `02-agents/Agents/Agent_Step06_DependencyInjection`
- `02-agents/Agents/Agent_Step11_Middleware`
- `02-agents/Agents/Agent_Step13_ChatReduction`, `Agent_Step18_CompactionPipeline` (read; apply if context grows)

Tasks
- [ ] Implement `LgtmTelemetryQuery.QueryLogsAsync` (Loki) + tests with recorded payloads.
- [ ] Implement `QueryMetricAsync` (Prometheus) + tests.
- [ ] Implement `GetTraceAsync` (Tempo) + tests.
- [ ] Run the CLI against the lab with s01 active. Iterate on tool outputs until they are compact and useful.
- [ ] Structured output: produce a `Diagnosis` with `RunAsync<Diagnosis>` (add a `/diagnose <service>` command
      or a separate method used by the eval runner).
- [ ] Middleware: tool-call budget per run; log result sizes; warn on large outputs.
- [ ] Agent OpenTelemetry: wrap the agent with OTel (see `Agent_Step05_Observability`) and send it to LGTM too.
      You will see your agent's traces next to the app's.
- [ ] Create `evals/IncidentResponder.Evals` (console): runs scenarios as described in `evals/README.md`,
      records accuracy, tool calls, tokens, cost, latency.
- [ ] Baseline: run s01 and s02 ×3 → `evals/results/phase-1.md`.
- [ ] Notes: `docs/notes/chatclientagent.md`, `tools-design.md`, `middleware.md`, `structured-output.md`.

Exit criteria
- s01 and s02 diagnosed correctly in at least 2 of 3 runs each; cost per diagnosis known.

Blog: *"An on-call assistant with Microsoft Agent Framework: tools that don't drown the model"* (tool design,
summaries vs dumps, middleware, first eval numbers).

---

## Phase 2 — Knowledge (week 4)

**Goal:** the agent knows the team's runbooks and remembers past incidents; its tools become reusable over MCP.

MAF concepts: MCP (consume + expose), RAG (`TextSearchProvider` / custom vector store), `AIContextProvider`,
memory providers.

Samples
- `02-agents/ModelContextProtocol/Agent_MCP_Server` (and `Agent_MCP_PerRun_AuthHeaders` for later)
- `02-agents/Agents/Agent_Step07_AsMcpTool`
- `02-agents/AgentWithRAG/AgentWithRAG_Step01_BasicTextRAG`, `Step02_CustomVectorStoreRAG`, `Step03_CustomRAGDataSource`
- `02-agents/AgentWithMemory/AgentWithMemory_Step01_ChatHistoryMemory`, `Step07_FileMemoryProvider`
- `02-agents/Agents/Agent_Step17_AdditionalAIContext`

Tasks
- [ ] Create `IncidentResponder.McpServer` exposing the telemetry tools (same descriptions). Agent consumes them
      over MCP instead of in-process. Try it from Claude Code too.
- [ ] RAG over `docs/runbooks/` (start simple: in-memory vector store; note the trade-offs).
- [ ] Incident memory: when a diagnosis is confirmed, store a short summary; on new incidents, surface similar
      past ones as additional context.
- [ ] Evals: compare with Phase 1 (does the runbook help? does memory help or bias?).
- [ ] Notes: `mcp.md`, `rag.md`, `context-providers.md`.

Exit criteria
- Same or better accuracy than Phase 1; tools work over MCP; runbooks cited in evidence.

Blog: *"Giving the agent a team memory: MCP tools, runbook RAG and incident memory"*.

---

## Phase 3 — Workflow (weeks 5–7) — the core of the project

**Goal:** the full process as a workflow: triage → parallel investigation → aggregation → remediation proposal
→ human approval → execute → verify. Survives restarts. Then a Magentic version and a measured comparison.

MAF concepts: `WorkflowBuilder`, `Executor<TIn,TOut>`, edges (conditional, switch, fan-out/fan-in), shared
state, streaming events, agents as executors, `RequestPort` (HITL), checkpoints, sub-workflows, workflow as
agent, Magentic orchestration, workflow observability/visualization.

Samples
- `03-workflows/_StartHere/01_Streaming` … `07_WriterCriticWorkflow` (all of them, in order)
- `03-workflows/ConditionalEdges/01_EdgeCondition`, `02_SwitchCase`, `03_MultiSelection`
- `03-workflows/Concurrent/Concurrent`, `Concurrent/MapReduce`
- `03-workflows/SharedStates`
- `03-workflows/HumanInTheLoop/HumanInTheLoopBasic`
- `03-workflows/Checkpoint/CheckpointAndResume`, `CheckpointAndRehydrate`, `CheckpointWithHumanInTheLoop`
- `03-workflows/Agents/CustomAgentExecutors`, `Agents/WorkflowAsAnAgent`, `Agents/GroupChatToolApproval`
- `03-workflows/Orchestration/Magentic`, `Orchestration/Handoff`
- `03-workflows/Observability/AspireDashboard`, `03-workflows/Visualization`
- `02-agents/Agents/Agent_Step01_UsingFunctionToolsWithApprovals` (tool-level approval)

Tasks
- [ ] Create `IncidentResponder.Workflows`. Start with a 3-executor sequential version (triage → investigate →
      diagnose) streaming events to the console.
- [ ] Triage executor: alert → list of hypotheses (structured output).
- [ ] Fan-out: one investigator per hypothesis (DB, recent deploy, dependencies, resources), each a
      `ChatClientAgent` with a narrow tool set and prompt. Fan-in barrier → aggregator ranks evidence.
      Add timeouts so a stuck investigator doesn't block the barrier.
- [ ] Remediation: a proposal (`RemediationProposal`) → `RequestPort` → approve / reject / modify.
- [ ] Remediation tools (lab only): `ClearFault`, `RestartService` — wrapped with tool-level approval too.
- [ ] Verify step: re-check the metric after remediation; loop back if not resolved (bounded).
- [ ] Checkpoints: persistent checkpoint store; kill the process while waiting for approval; resume without
      re-investigating. Measure tokens saved.
- [ ] Visualize the workflow graph (export) for the README/blog.
- [ ] Magentic variant: manager + analysts (logs, metrics, traces, deploy history). Same scenarios.
- [ ] Evals: fan-out vs Magentic — accuracy, cost, latency, tool calls. → `evals/results/phase-3.md`.
- [ ] Notes: `workflows-basics.md`, `fan-out.md`, `hitl.md`, `checkpoints.md`, `magentic.md`.

Exit criteria
- End-to-end run with approval works; resume after kill works; comparison table with real numbers.

Blog: two posts — *"From agent to workflow: fan-out, human approval and checkpoints"* and
*"Fixed graph vs Magentic: the same incidents, measured"*.

---

## Phase 4 — Productization (weeks 8–9)

**Goal:** a "war room" web UI, durable escalation, auth, full observability of the agents, cloud evals,
and a second agent talking over A2A.

MAF concepts: hosting (ASP.NET), AG-UI (backend tools, frontend tools, HITL, shared state), durable
agents/workflows, evaluation (custom + Foundry), A2A, Foundry hosted agents, DevUI.

Samples
- `02-agents/AGUI/Step01_GettingStarted` … `Step05_StateManagement`
- `05-end-to-end/AGUIWebChat`, `05-end-to-end/AGUIClientServer`
- `05-end-to-end/AspNetAgentAuthorization`
- `04-hosting/af-hosting/local_responses`, `local_responses_workflow`
- `04-hosting/DurableAgents`, `04-hosting/DurableWorkflows` (READMEs → `microsoft/agent-framework-durable-extension`)
- `02-agents/Evaluation/*`, `03-workflows/Evaluation/*`, `05-end-to-end/Evaluation/*`
- `05-end-to-end/A2AClientServer`, `02-agents/A2A/*`
- `04-hosting/FoundryHostedAgents`
- `02-agents/DevUI/DevUI_Step01_BasicUsage`, `05-end-to-end/DevUIAspireIntegration`

Tasks
- [ ] `IncidentResponder.Host` (ASP.NET) exposing the workflow over AG-UI; add it to the AppHost.
- [ ] War room UI: chat + live `IncidentState` (timeline, hypotheses, evidence) via shared state + approve/reject
      buttons (HITL) + one frontend tool (e.g. render a latency chart).
- [ ] Durable escalation: if no approval in N minutes → escalate (timer). Compare with your own checkpoint-based
      version from Phase 3.
- [ ] Auth: roles `viewer` (read) and `oncall` (can approve). Only `oncall` answers the RequestPort.
- [ ] A2A: a separate "Deploy agent" (owns deployment history / rollback) the responder calls over A2A.
- [ ] Evals: custom evaluators + Foundry evaluators on the explanation quality; keep the objective metrics too.
- [ ] Infra as code in `infra/` (Bicep or azd) for the Foundry resources; `azd down` documented.
- [ ] Optional: deploy one agent as a Foundry hosted agent.

Exit criteria
- Demo: inject s01 → war room shows the investigation live → approve → resolved → numbers in the eval report.

Blog: *"An agent war room: AG-UI, durable timers and A2A with Microsoft Agent Framework"*.

---

## Phase 5 — Postmortem (weeks 10–11)

**Goal:** an autonomous agent that writes a good postmortem from the incident record, with real impact numbers.

MAF concepts: Harness (`AsHarnessAgent`, `TodoProvider`, `AgentModeProvider`, `FileAccessProvider`,
background agents), CodeAct, `LoopAgent` + `LoopEvaluator`.

Samples
- `02-agents/Harness/Harness_Step01_Research` … `Harness_Step05_Loop`
- `02-agents/Harness/BuildYourOwnClaw/*` (4 steps; there is a matching blog series)
- `02-agents/AgentWithCodeAct/*`

Tasks
- [ ] Export the incident record from the workflow (timeline, evidence, decisions) to files.
- [ ] `IncidentResponder.Postmortem`: harness agent with a todo per section (summary, timeline, root cause,
      impact, what went well/badly, action items).
- [ ] CodeAct to compute impact (duration, failed requests, error budget burned) from exported metrics.
- [ ] `LoopAgent` with an AI-judge evaluator against a postmortem rubric (blameless, specific, actionable).
- [ ] Compare a few postmortems with and without the loop.
- [ ] Retro of the whole project → `blog/retro.md`.

Exit criteria
- Postmortems for s01–s03 that you would accept from a teammate.

Blog: *"Letting an agent write the postmortem (and grading it)"* + the retrospective.

---

## After this

Phase 2 of the learning path: apply MAF to the incremental legacy migration idea (Strangler Fig + YARP +
characterization tests as objective evals). Separate repo.
