# 0003 — Model access through IChatClient

- Status: accepted
- Date: 2026-11-10

## Context

Most MAF samples create agents straight from `AIProjectClient.AsAIAgent(...)` (Foundry package). MAF's core
abstraction, though, is `ChatClientAgent` over any `IChatClient` (Microsoft.Extensions.AI).

## Decision

All agents are built from an `IChatClient` created in one place, `ChatClientFactory`. Phase 1 uses Foundry's
OpenAI Responses endpoint:
`AIProjectClient → GetProjectOpenAIClient() → GetResponsesClient() → AsIChatClient(deployment)`
(same chain as the Harness samples).

Foundry-specific agent types (server-side agents and conversations, Foundry memory, hosted tools) are introduced
only in the phase that needs them (Phase 4), on purpose and documented.

## Consequences

- (+) Deterministic tests with `FakeChatClient`; CI needs no Azure.
- (+) Switching provider/model is a one-place change, useful to compare models in evals.
- (−) Some Foundry samples need a small adaptation when copied (replace `AsAIAgent` on the project client with
  the factory + `IChatClient.AsAIAgent`).
