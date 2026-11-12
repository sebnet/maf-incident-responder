# LGTM: what the telemetry APIs expose

Reference for implementing `LgtmTelemetryQuery` in Phase 1. Verified 2026-11-11 against
`grafana/otel-lgtm:0.40.0` with the lab running (OpenTelemetry .NET SDK 1.21.0, OTLP over http/protobuf).

## Reachability

All three APIs answer directly from the host. Grafana's datasource proxy is **not** needed.

| Backend | Host URL | Smoke test |
|---|---|---|
| Loki | `http://localhost:3100` | `GET /loki/api/v1/labels` |
| Prometheus | `http://localhost:9090` | `GET /api/v1/query?query=up` |
| Tempo | `http://localhost:3200` | `GET /api/search?q={}` (TraceQL) |
| Grafana UI | `http://localhost:3000` | — |

The OTLP receiver (container port 4318) has no fixed host port: Aspire assigns one and passes it to the services.

ADR 0002 check: the `OTEL_EXPORTER_OTLP_ENDPOINT` override in the AppHost wins. The Aspire dashboard receives no
telemetry from the services (`aspire otel logs` → "No logs found"), and LGTM receives all of it.

## Service identity (same everywhere)

`service_name` = the Aspire resource name: `orders-api`, `catalog-api`, `loadgen`.
`service_instance_id` is a random Aspire suffix (e.g. `mqmqrbpb`) that **changes on every AppHost run**: never
filter or group by it in tools or evals.

## Loki (logs)

**Index labels (stream selectors): only `service_name` and `service_instance_id`.**
Everything else is OTLP structured metadata: filter it with a pipeline stage, not in `{...}`.

```logql
{service_name="orders-api"} | detected_level=~"error|warn"
sum by (service_name, detected_level) (count_over_time({service_name=~".+"}[5m]))
```

Useful structured metadata per line:

| Field | Example | Note |
|---|---|---|
| `detected_level` | `info`, `warn`, `error` | lowercase, derived by Loki. Use this for level filters. |
| `severity_text` / `severity_number` | `Information` / `9` | raw OTel severity |
| `trace_id`, `span_id` | `f817f0...` | lowercase OTel ids → jump to Tempo |
| `scope_name` | `Microsoft.AspNetCore.Hosting.Diagnostics` | the .NET logger category |
| `_OriginalFormat_` | `Order {OrderId} created ...` | message template: good for grouping similar errors |
| message-template args | `StatusCode`, `Path`, `OrderId`, `ElapsedMilliseconds`... | one field per placeholder |

The log line itself is the rendered message.

Exceptions: logs that carry one have `exception_type` with the **short** type name (`HttpRequestException`, not
`System.Net.Http.HttpRequestException`). Lines without an exception have no such field, so a `sum by
(exception_type)` returns an extra series with empty labels.

Polly (standard resilience handler) logs every failed attempt; the **last** one, when retries give up, at Error
level with no exception attached:

| Field | Example |
|---|---|
| `_OriginalFormat_` | `Execution attempt. Source: '{PipelineName}/...', ... Result: '{Result}', ... Attempt: '{Attempt}' ...` |
| `scope_name` | `Polly` |
| `StrategyName` | `Standard-Retry` |
| `Result`, `Attempt`, `Handled` | `500`, `3`, `true` |
| `Uri` | `https+http://catalog-api/items/1` (the logical, service-discovery name of the dependency) |

During s01 (verified 2026-11-12) orders-api error logs are exactly two templates in equal numbers: the
unhandled-exception line (`DeveloperExceptionPageMiddleware`, `exception_type` = `HttpRequestException`) and this
Polly line. Its `Uri` names the failing dependency: worth surfacing in the log tool's summary.

Volume: `Microsoft.AspNetCore`, `System.Net.Http.HttpClient`, `Polly` and `Npgsql` log at Warning and above (ServiceDefaults).
Before that, orders-api wrote about 1,400 `info` lines per minute; now it writes about 12 (its own "Order created" lines). During s01 the
logs are mostly unhandled-exception errors and Polly retry warnings: signal, not noise. Log tools must still
count and group, never return raw lines (AGENTS.md §6.4).

## Prometheus (metrics)

`job` = `service_name`. Names follow OTel semantic conventions, converted to Prometheus names (dots → `_`, unit
suffix, `_total` for counters). Durations are in **seconds**.

Most relevant for the scenarios:

| Metric | Labels | Used for |
|---|---|---|
| `http_server_request_duration_seconds_{bucket,count,sum}` | `http_route`, `http_request_method`, `http_response_status_code` | inbound error rate, p95 |
| `http_client_request_duration_seconds_{bucket,count,sum}` | `server_address`, `server_port`, `http_response_status_code`, `error_type` | outbound calls (orders → catalog) — **includes the OTLP exporter**, see below |
| `http_server_active_requests`, `kestrel_queued_connections` | — | saturation |
| `dotnet_thread_pool_queue_length`, `dotnet_gc_*`, `dotnet_exceptions_total` | — | runtime health |
| `db_client_connection_count` | `db_client_connection_state` (`idle`/`used`), `db_client_connection_pool_name` | Postgres pool usage (orders-api) |
| `db_client_connection_max` | `db_client_connection_pool_name` | pool size (20) |
| `db_client_operation_duration_seconds_*` | — | query latency |

`db_client_connection_pool_name` is the connection string **without the password** (Npgsql strips it). It contains
the dynamic Postgres port, so don't filter by it: use `service_name`.

```promql
# pool saturation (1.0 = exhausted)
sum by (service_name) (db_client_connection_count{db_client_connection_state="used"})
  / sum by (service_name) (db_client_connection_max)
```

```promql
# error rate per service
sum by (service_name) (rate(http_server_request_duration_seconds_count{http_response_status_code=~"5.."}[5m]))
  / sum by (service_name) (rate(http_server_request_duration_seconds_count[5m]))
# p95 latency (seconds)
histogram_quantile(0.95, sum by (le, service_name) (rate(http_server_request_duration_seconds_bucket[5m])))
```

Baseline with no fault: about 3.8 req/s on both APIs, p95 about 5 ms, no 5xx.

`http_client_*` counts **every** HttpClient call of the service, including the OpenTelemetry exporter sending
OTLP to LGTM (~1.9 req/s of 200s from orders-api). `server_address` is `localhost` for both, and the ports are
dynamic (Aspire), so neither identifies the dependency by name. A tool must not report "outbound calls" as a
plain sum: group by `server_port` and drop the OTLP series, or rely on the Polly log `Uri` / Tempo spans for the
dependency name. TODO(F1): decide how the metrics tool tells them apart.

During s01 (60 % errors on catalog-api, verified 2026-11-12):

| | catalog-api | orders-api |
|---|---|---|
| 5xx rate (5 min) | 0.61 | 0.12 |
| req/s | ~0.68 | ~0.35 |
| p95 | 33 ms | 10 s |

- Traffic collapses from 3.8 to ~0.35 req/s: the LoadGen sends one request at a time, and each failing order
  waits through the retries. That keeps catalog-api at ~18 calls per 30 s (max 34), far below the circuit
  breaker's minimum throughput of 100, so **the breaker never opens** and there is no `BrokenCircuitException`.
- orders-api also returns **499** on `/orders` (about twice as many as 500s): the LoadGen's own 10 s attempt
  timeout cancels slow requests. The usual error-rate query (`5..`) does not count them.
`/health` and `/alive` do not appear in `http_route` (filtered in ServiceDefaults, as intended).

## Tempo (traces)

Search with TraceQL: `GET /api/search?q=<traceql>&start=<unix s>&end=<unix s>&limit=N`; full trace:
`GET /api/traces/<traceId>`.

| Scope | Tags |
|---|---|
| resource | `service.name`, `service.instance.id`, `telemetry.sdk.*` |
| span | `http.request.method`, `http.response.status_code`, `http.route`, `url.full`, `url.path`, `server.address`, `server.port` |
| intrinsic | `name`, `status`, `duration`, `kind`, `rootServiceName`, `traceDuration`, ... |

```traceql
{ resource.service.name = "catalog-api" && span.http.route = "/items/{id:int}" }
{ status = error }
{ resource.service.name = "orders-api" && duration > 500ms }
```

A typical trace: `loadgen POST` → `orders-api POST /orders` → `orders-api GET` (HttpClient) →
`catalog-api GET /items/{id:int}`, plus an `orders-api` `postgresql` span (scope `Npgsql`) with `db.query.text`,
`db.namespace` = `ordersdb`. The root is `loadgen`, so search by `resource.service.name` for the span you
care about, not by `rootServiceName`.

## Gotchas

- Loki timestamps in responses are nanoseconds as strings; query params accept unix seconds or RFC3339.
- Prometheus `job` duplicates `service_name`; prefer `service_name` (same label as Loki).
- Tempo and Loki use lowercase hex trace ids: no conversion needed to correlate.
