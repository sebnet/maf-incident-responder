# 0002 — Grafana LGTM as the local telemetry backend

- Status: accepted (ports to be verified in Phase 0)
- Date: 2026-11-10

## Context

The responder must *query* logs, metrics and traces over an API. The Aspire dashboard is excellent for a human
but is not meant to be queried programmatically by an agent. Azure Application Insights (KQL) is closer to what
companies run, but adds cloud dependency and cost from day one.

## Decision

Run `grafana/otel-lgtm` (OTel collector + Loki + Prometheus + Tempo + Grafana) as a container from the Aspire
AppHost. Services export OTLP to it by overriding `OTEL_EXPORTER_OTLP_ENDPOINT`.

## Consequences

- (+) Free, local, real query languages (LogQL, PromQL, TraceQL) and real HTTP APIs.
- (+) Grafana UI to see what the agent sees.
- (−) The Aspire dashboard no longer receives the services' telemetry (it still shows resources and console
  logs). If both are wanted later, put an OTel collector in front that fans out to both.
- (−) Label names depend on the image version; document them in `docs/notes/lgtm-labels.md`.
- Later option (Phase 4): an `ITelemetryQuery` over Application Insights (KQL) to show the same agent against
  an enterprise backend. The abstraction in Core makes that a new implementation, not a rewrite.
