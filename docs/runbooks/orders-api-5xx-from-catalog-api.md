# orders-api 5xx caused by catalog-api

Owner: orders team (alert), catalog team (usually the fix). Last reviewed: 2026-11.

`POST /orders` calls catalog-api (`GET /items/{id}`) to get the price before saving the order. If catalog-api
returns 5xx, orders-api retries (standard resilience handler, a few attempts with backoff) and then gives up
with a 500 of its own. So the alert fires on **orders-api**, but orders-api is usually just the messenger.

## Signals

- Alert: orders-api error rate > 5%.
- orders-api logs: unhandled `HttpRequestException` ("Response status code does not indicate success: 500")
  from `CatalogClient`, plus Polly retry warnings. Few or no database errors.
- If catalog-api fails hard enough, the circuit breaker in orders-api opens (>10% failures over 30 s, then
  open for 5 s). While open, orders-api fails instantly with `BrokenCircuitException` and **never calls
  catalog-api**: those traces have no catalog-api span. Same incident, different message — don't treat it as a
  second problem.
- catalog-api has its own 5xx at the same time (often a higher rate than orders-api, because retries hide part of it).
- Traces: the failing span is the `catalog-api GET /items/{id:int}` child, with an exception event.

## How to confirm

1. Compare error rates of both services over the same window:
   ```promql
   sum by (service_name) (rate(http_server_request_duration_seconds_count{http_response_status_code=~"5.."}[5m]))
     / sum by (service_name) (rate(http_server_request_duration_seconds_count[5m]))
   ```
2. Check orders-api outbound calls to catalog-api (`http_client_request_duration_seconds_count` by
   `server_address`, `http_response_status_code`). 500s here = it's the dependency.
3. Open one failing trace in Tempo (`{ resource.service.name = "catalog-api" && status = error }`) and read the
   exception message on the catalog-api span. That message is what you put in the incident channel.
4. Group catalog-api error logs by message template. One dominant message = one cause.
5. Seeing only `BrokenCircuitException` in orders-api? The breaker hides the original error: look at
   catalog-api logs/traces from just before the circuit opened.

If catalog-api is clean and orders-api still fails, this is not your runbook: look at the database
(see `orders-api-postgres-pool-exhausted.md`) or at orders-api itself.

## Mitigation

- Do **not** restart orders-api. It won't help and you lose the retry/backoff state.
- Recent catalog-api deploy or config change? Roll it back first, ask questions later.
- No recent change: restart catalog-api instances one at a time (it reloads the catalog on start). Watch the
  error rate for 5 min.
- Still failing after restart: escalate. Data problems in the catalog are not something on-call fixes.

## Escalate to

- Catalog team (#team-catalog) — anything that needs touching catalog data.
- Orders team only if catalog-api is healthy and orders-api still returns 5xx.

<!-- TODO: link the Grafana dashboard once we have one that isn't Explore. -->
