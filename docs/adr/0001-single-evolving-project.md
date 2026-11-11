# 0001 — One evolving project instead of separate demos

- Status: accepted
- Date: 2026-11-10

## Context

The goal is to learn Microsoft Agent Framework in depth (~11 weeks, ~15 h/week) and publish the journey.
Options: (a) a set of independent demo projects, one per concept; (b) one system that grows phase by phase.

## Decision

One evolving system (the Incident Responder), built in phases 0–5. Each phase adds MAF capabilities to the same
code base. Isolated experiments go to `spikes/` before being integrated.

## Consequences

- (+) Components get reused and refactored as concepts are learned (the Phase 1 agent becomes a Phase 3
  executor; in-process tools move behind MCP). That composition is a large part of what there is to learn.
- (+) The same eval scenarios run in every phase, so improvement is measurable and can be charted.
- (+) The blog tells one continuous story.
- (−) Early decisions constrain later phases. Mitigation: small, simple contracts (`Diagnosis`,
  `ITelemetryQuery`) and refactoring freely.
- (−) Older phases are no longer "runnable as they were". Mitigation: git tag per phase; keep old entry points
  (the Phase 1 CLI) working.
