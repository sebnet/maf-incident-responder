# MAF Incident Responder

Learning **Microsoft Agent Framework (.NET)** by building one system that grows phase by phase: an agent that
investigates production incidents in a lab app, finds the root cause from logs, metrics and traces, proposes a
fix, waits for a human to approve it, and writes the postmortem.

> Status: **Phase 0 — lab**. See [`docs/PLAN.md`](docs/PLAN.md).

## Why an incident responder?

It exercises most of MAF for a real reason, not as a checkbox:

| Need | MAF concept |
|---|---|
| Query logs/metrics/traces | function tools, middleware, MCP |
| Use team runbooks and past incidents | RAG, context providers, memory |
| Investigate several hypotheses at once | workflows, fan-out / fan-in |
| Follow clues dynamically | Magentic orchestration |
| Never restart anything without a human | tool approvals, `RequestPort` (human-in-the-loop) |
| Incidents last hours | checkpoints, durable execution |
| A live "war room" | AG-UI, shared state |
| Write the postmortem | Harness, CodeAct, loop agents |

And it can be **evaluated objectively**: faults are injected on purpose, so the true root cause is known.

## Phases

| # | Phase | Adds |
|---|---|---|
| 0 | Lab | Aspire app that fails on demand + Grafana LGTM |
| 1 | On-call assistant | Single agent with telemetry tools, structured diagnosis, eval runner |
| 2 | Knowledge | MCP server, runbook RAG, incident memory |
| 3 | Workflow | Parallel investigation, human approval, checkpoints; Magentic comparison |
| 4 | Productization | AG-UI war room, durable escalation, auth, A2A, cloud evals |
| 5 | Postmortem | Harness agent that writes and self-grades the postmortem |

## Run

Prerequisites: .NET 10 SDK, Docker Desktop, an Azure subscription with a Foundry project and a model deployment
(Phase 1+), Azure CLI (`az login`).

```bash
dotnet test --solution IncidentResponder.slnx            # deterministic tests, no Azure
dotnet run --project src/Lab/IncidentLab.AppHost        # the lab + Grafana on http://localhost:3000
```

Phase 1 CLI: see `AGENTS.md` §8.

## Repo guide

- [`docs/PLAN.md`](docs/PLAN.md): phases, tasks, which MAF samples to study for each
- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) and [`docs/adr/`](docs/adr/): design and decisions
- [`docs/notes/`](docs/notes/): one note per MAF concept learned
- [`evals/`](evals/): scenarios with known root cause and results per phase
- [`blog/`](blog/): post drafts

## License

MIT (add a LICENSE file before publishing).
