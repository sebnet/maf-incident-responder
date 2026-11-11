# orders-api slow, no errors

Owner: orders team. Last reviewed: 2026-11.

Normal p95 for orders-api is single-digit milliseconds. When it goes to seconds without errors, users
see a spinner, not an error page, so this one is easy to miss and easy to misdiagnose. **Don't go looking for
an error that isn't there.**

## Signals

- Alert: orders-api p95 latency > 1500 ms.
- Error rate flat (≈ 0). Error logs: nothing new.
- `http_server_active_requests` up (requests pile up while they wait).
- Usually affects all routes of the service, not one endpoint.

## How to confirm

1. p95 per service — is it only orders-api, or catalog-api too?
   ```promql
   histogram_quantile(0.95, sum by (le, service_name) (rate(http_server_request_duration_seconds_bucket[5m])))
   ```
2. Is the time spent in a dependency? Check, for orders-api:
   - outbound to catalog-api: `http_client_request_duration_seconds` p95 by `server_address`;
   - Postgres: `db_client_operation_duration_seconds` p95, and pool usage
     (`db_client_connection_count{db_client_connection_state="used"}` vs `db_client_connection_max`).
3. Look at a few slow traces (`{ resource.service.name = "orders-api" && duration > 1s }`). Compare the server
   span with its children:
   - a child span is slow → the time is in that dependency, follow it;
   - children are fast and there is a big gap **before** or between them → the time is spent inside orders-api
     itself (thread pool starvation, GC pauses, lock contention, something blocking in the request pipeline).
4. Runtime metrics for the second case: `dotnet_thread_pool_queue_length`, `dotnet_gc_*`.

Pool at 100% + slow requests is not this runbook: see `orders-api-postgres-pool-exhausted.md`.

## Mitigation

- Recent orders-api deploy or config change? Roll back.
- Time spent inside orders-api with no clear cause: restart the instances one at a time. If latency comes back
  later, capture a dump (`dotnet-dump`) before restarting next time, and hand it to the orders team.
- Dependency slow: follow that dependency's runbook / owner.
- Scaling out buys time if it's load; it does nothing if every request waits the same fixed time.

## Escalate to

- Orders team (#team-orders) if it persists > 30 min or comes back after a restart.
