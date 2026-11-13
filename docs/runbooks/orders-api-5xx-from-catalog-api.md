# orders-api 5xx caused by catalog-api

Owner: orders team (alert), catalog team (usually the fix). Last reviewed: 2026-11.

`POST /orders` calls catalog-api (`GET /items/{id}`) to get the price before saving the order. If catalog-api
returns 5xx, orders-api retries (standard resilience handler, a few attempts with backoff) and then gives up
with a 500 of its own. So the alert fires on **orders-api**, but orders-api is usually just the messenger.

## Signals

- Alert: orders-api error rate > 5%.
- orders-api logs, roughly one pair per failed order:
  - unhandled `HttpRequestException` ("Response status code does not indicate success: 500") from `CatalogClient`;
  - a Polly error "Execution attempt ... Result: '500' ... Attempt: '3'" (the last retry giving up). Its `Uri`
    field is the catalog-api URL — the quickest pointer to the dependency.
  Few or no database errors.
- orders-api latency goes up too (p95 of several seconds): failed orders wait through the retries and their
  backoff. Callers with shorter timeouts give up first, so you also see 499s on `/orders`. Don't open a
  separate latency incident.
- catalog-api has its own 5xx at the same time (often a much higher rate than orders-api, because retries hide
  most of it).
- Circuit breaker: it opens at >10% failures over 30 s, with at least 100 calls in that window. Our usual
  traffic is below that most of the time, but when the incident lasts it **does open now and then** (seen every
  20–60 min): a Polly "Resilience event ... 'OnCircuitOpened'" error, then a few `BrokenCircuitException`s
  instead of the `HttpRequestException` above. While open, orders-api fails instantly and **never calls
  catalog-api**: those traces have no catalog-api span. Same incident, different message — don't treat it as a
  second problem, and don't disable the breaker: it is protecting catalog-api.
- Traces: the failing span is the `catalog-api GET /items/{id:int}` child, with an exception event.

## How to confirm

1. Compare error rates of both services over the same window:
   ```promql
   sum by (service_name) (rate(http_server_request_duration_seconds_count{http_response_status_code=~"5.."}[5m]))
     / sum by (service_name) (rate(http_server_request_duration_seconds_count[5m]))
   ```
2. Check orders-api outbound calls to catalog-api (`http_client_request_duration_seconds_count` by
   `server_address`, `server_port`, `http_response_status_code`). 500s here = it's the dependency. Careful: this
   metric also counts the telemetry exporter's own calls (a steady series of 200s to another port). Ignore it,
   or the outbound traffic looks much healthier than it is.
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
