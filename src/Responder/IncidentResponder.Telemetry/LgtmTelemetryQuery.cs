using IncidentResponder.Core;

namespace IncidentResponder.Telemetry;

/// <summary>Base URLs of the LGTM stack started by the AppHost.</summary>
public sealed record LgtmOptions(Uri Loki, Uri Prometheus, Uri Tempo)
{
    public static LgtmOptions LocalDefaults { get; } = new(
        new Uri("http://localhost:3100"),
        new Uri("http://localhost:9090"),
        new Uri("http://localhost:3200"));
}

/// <summary>
/// <see cref="ITelemetryQuery"/> over Grafana LGTM.
///
/// TODO(F1) — implement, in this order, with a unit test per method using a fake HttpMessageHandler:
///  1. QueryLogsAsync  → Loki  GET /loki/api/v1/query_range  (LogQL: {service_name="orders-api"} | detected_level="error")
///                        group lines by normalized message, count, keep top N + one sample trace id.
///  2. QueryMetricAsync → Prometheus GET /api/v1/query_range (PromQL over http_server_request_duration_seconds_*),
///                        map Metrics.ErrorRate / P95LatencyMs / RequestRate to queries, downsample to ~1 point/min.
///  3. GetTraceAsync   → Tempo GET /api/traces/{traceId}, keep slowest 5 spans and all error spans.
///
/// Label names (service_name vs service.name, level vs detected_level) depend on the otel-lgtm version:
/// check them in Grafana Explore first and write what you find in docs/notes/.
/// </summary>
public sealed class LgtmTelemetryQuery(HttpClient http, LgtmOptions options) : ITelemetryQuery
{
    private readonly HttpClient _http = http;
    private readonly LgtmOptions _options = options;

    public Task<LogSummary> QueryLogsAsync(LogQuery query, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException($"TODO(F1): Loki query_range against {_options.Loki} via {_http.GetType().Name}");

    public Task<MetricSummary> QueryMetricAsync(MetricQuery query, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException($"TODO(F1): Prometheus query_range against {_options.Prometheus}");

    public Task<TraceSummary?> GetTraceAsync(string traceId, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException($"TODO(F1): Tempo /api/traces against {_options.Tempo}");
}
