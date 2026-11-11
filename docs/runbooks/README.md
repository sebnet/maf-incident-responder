# Runbooks

One runbook per failure mode, written the way an SRE team would (Phase 0 task). In Phase 2 they become the
RAG corpus of the responder.

| Runbook | Failure mode |
|---|---|
| [orders-api-5xx-from-catalog-api.md](orders-api-5xx-from-catalog-api.md) | orders-api errors caused by a failing dependency |
| [orders-api-high-latency.md](orders-api-high-latency.md) | orders-api slow with no errors |
| [orders-api-postgres-pool-exhausted.md](orders-api-postgres-pool-exhausted.md) | orders-api cannot get Postgres connections |

Suggested format:

```markdown
# <Symptom>, e.g. "orders-api 5xx caused by catalog-api"

## Signals
## How to confirm
## Mitigation
## Escalate to
```

Keep them short and a bit imperfect, like real ones. Do NOT write them as an answer key for the eval scenarios
(e.g. don't name the exact injected message): the point is to see whether general team knowledge helps.
