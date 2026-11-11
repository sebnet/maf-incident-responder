namespace IncidentResponder.Core;

/// <summary>
/// Read-only access to telemetry. Implementations MUST return summaries (grouped, counted, top-N),
/// never raw dumps: whatever comes back goes straight into the model's context window.
/// </summary>
public interface ITelemetryQuery
{
    Task<LogSummary> QueryLogsAsync(LogQuery query, CancellationToken cancellationToken = default);

    Task<MetricSummary> QueryMetricAsync(MetricQuery query, CancellationToken cancellationToken = default);

    Task<TraceSummary?> GetTraceAsync(string traceId, CancellationToken cancellationToken = default);
}

public sealed record LogQuery(string Service, TimeWindow Window, string MinLevel = "error", string? Contains = null, int Top = 10);

public sealed record LogGroup(string Message, int Count, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, string? SampleTraceId);

public sealed record LogSummary(string Service, TimeWindow Window, int TotalMatches, IReadOnlyList<LogGroup> TopGroups);

/// <summary>Known metric names the tools expose. Keep the list short and meaningful.</summary>
public static class Metrics
{
    public const string ErrorRate = "error_rate";
    public const string P95LatencyMs = "p95_latency_ms";
    public const string RequestRate = "request_rate";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { ErrorRate, P95LatencyMs, RequestRate };
}

public sealed record MetricQuery(string Service, string Metric, TimeWindow Window);

public sealed record MetricPoint(DateTimeOffset At, double Value);

/// <summary>Aggregates plus a coarse series (e.g. 1 point per minute), never every sample.</summary>
public sealed record MetricSummary(string Service, string Metric, TimeWindow Window, double Min, double Max, double Avg, double Last, IReadOnlyList<MetricPoint> Series);

public sealed record SpanSummary(string Service, string Name, TimeSpan Duration, string? Error);

public sealed record TraceSummary(string TraceId, TimeSpan Duration, IReadOnlyList<SpanSummary> SlowestSpans, IReadOnlyList<SpanSummary> ErrorSpans);
