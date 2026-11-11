using IncidentResponder.Core;

namespace IncidentResponder.Tests.Fakes;

/// <summary>Canned telemetry that records every query it receives.</summary>
public sealed class StubTelemetryQuery : ITelemetryQuery
{
    public List<LogQuery> LogQueries { get; } = [];
    public List<MetricQuery> MetricQueries { get; } = [];
    public List<string> TraceQueries { get; } = [];

    public LogSummary? Logs { get; set; }

    public Task<LogSummary> QueryLogsAsync(LogQuery query, CancellationToken cancellationToken = default)
    {
        LogQueries.Add(query);
        return Task.FromResult(Logs ?? new LogSummary(query.Service, query.Window, 0, []));
    }

    public Task<MetricSummary> QueryMetricAsync(MetricQuery query, CancellationToken cancellationToken = default)
    {
        MetricQueries.Add(query);
        return Task.FromResult(new MetricSummary(query.Service, query.Metric, query.Window, 0, 0, 0, 0, []));
    }

    public Task<TraceSummary?> GetTraceAsync(string traceId, CancellationToken cancellationToken = default)
    {
        TraceQueries.Add(traceId);
        return Task.FromResult<TraceSummary?>(null);
    }
}
