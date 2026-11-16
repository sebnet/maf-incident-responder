var builder = DistributedApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------------------------
// Telemetry backend: grafana/otel-lgtm = OTel collector + Loki (logs) + Prometheus (metrics)
// + Tempo (traces) + Grafana UI, in a single container. This is what the responder QUERIES.
// The Aspire dashboard is great for you, but it has no query API for the agent.
//
// Image tag pinned (never float versions). Ports and labels verified: docs/notes/lgtm-labels.md.
// ---------------------------------------------------------------------------------------------
var lgtm = builder.AddContainer("lgtm", "grafana/otel-lgtm", "0.40.0")
    .WithHttpEndpoint(port: 3000, targetPort: 3000, name: "grafana")
    // OTLP on host port 4319, not the standard 4318 (often taken by another Aspire dashboard or collector on the
    // machine). Lab services get it through GetEndpoint below; the responder CLI, which runs outside Aspire, needs a
    // fixed port to send the agent's own traces (src/Responder/IncidentResponder.Cli/appsettings.json).
    .WithHttpEndpoint(port: 4319, targetPort: 4318, name: "otlp-http")
    .WithHttpEndpoint(port: 3100, targetPort: 3100, name: "loki")
    .WithHttpEndpoint(port: 9090, targetPort: 9090, name: "prometheus")
    .WithHttpEndpoint(port: 3200, targetPort: 3200, name: "tempo")
    // Tempo with a larger attribute size limit, so the agent's spans keep their full prompts and tool results.
    // Relative path: resolved from the AppHost project directory.
    .WithBindMount("lgtm/tempo-config.yaml", "/otel-lgtm/tempo-config.yaml", isReadOnly: true);

var otlp = lgtm.GetEndpoint("otlp-http");

// orders-api stores orders in Postgres. No data volume: the database starts empty on every run, which is what a lab wants.
var postgres = builder.AddPostgres("postgres");
var ordersDb = postgres.AddDatabase("ordersdb");

// TODO(F2): wire Redis into catalog-api (cache) if a cache-related scenario is added.
var redis = builder.AddRedis("redis");

// Services send OTLP to LGTM instead of the Aspire dashboard. Trade-off documented in docs/adr/0002.
var catalog = builder.AddProject<Projects.IncidentLab_CatalogApi>("catalog-api")
    .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", otlp)
    .WithEnvironment("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf")
    .WaitFor(lgtm);

var orders = builder.AddProject<Projects.IncidentLab_OrdersApi>("orders-api")
    .WithReference(catalog)
    .WaitFor(catalog)
    .WithReference(ordersDb)
    .WaitFor(ordersDb)
    .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", otlp)
    .WithEnvironment("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf");

builder.AddProject<Projects.IncidentLab_LoadGen>("loadgen")
    .WithReference(orders)
    .WaitFor(orders)
    .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", otlp)
    .WithEnvironment("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf");

builder.Build().Run();
