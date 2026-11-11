var builder = DistributedApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------------------------
// Telemetry backend: grafana/otel-lgtm = OTel collector + Loki (logs) + Prometheus (metrics)
// + Tempo (traces) + Grafana UI, in a single container. This is what the responder QUERIES.
// The Aspire dashboard is great for you, but it has no query API for the agent.
//
// TODO(F0): verify the internal ports of the image (3100 Loki, 9090 Prometheus, 3200 Tempo).
// Alternative if they are not reachable: query through Grafana's datasource proxy on :3000.
// ---------------------------------------------------------------------------------------------
var lgtm = builder.AddContainer("lgtm", "grafana/otel-lgtm")
    .WithHttpEndpoint(port: 3000, targetPort: 3000, name: "grafana")
    // No fixed host port for OTLP: services get it through GetEndpoint below, and 4318 is often taken
    // (any other Aspire dashboard or collector on the machine). Fixed ports only where humans/the CLI need them.
    .WithHttpEndpoint(targetPort: 4318, name: "otlp-http")
    .WithHttpEndpoint(port: 3100, targetPort: 3100, name: "loki")
    .WithHttpEndpoint(port: 9090, targetPort: 9090, name: "prometheus")
    .WithHttpEndpoint(port: 3200, targetPort: 3200, name: "tempo");

var otlp = lgtm.GetEndpoint("otlp-http");

// TODO(F0): wire these into the services (orders -> Postgres, catalog -> Redis) for infra-level scenarios.
var postgres = builder.AddPostgres("postgres");
var ordersDb = postgres.AddDatabase("ordersdb");
var redis = builder.AddRedis("redis");

// Services send OTLP to LGTM instead of the Aspire dashboard. Trade-off documented in docs/adr/0002.
var catalog = builder.AddProject<Projects.IncidentLab_CatalogApi>("catalog-api")
    .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", otlp)
    .WithEnvironment("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf")
    .WaitFor(lgtm);

var orders = builder.AddProject<Projects.IncidentLab_OrdersApi>("orders-api")
    .WithReference(catalog)
    .WaitFor(catalog)
    .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", otlp)
    .WithEnvironment("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf");

builder.AddProject<Projects.IncidentLab_LoadGen>("loadgen")
    .WithReference(orders)
    .WaitFor(orders)
    .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", otlp)
    .WithEnvironment("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf");

builder.Build().Run();
