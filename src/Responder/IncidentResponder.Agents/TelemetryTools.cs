using System.ComponentModel;
using IncidentResponder.Core;
using Microsoft.Extensions.AI;

namespace IncidentResponder.Agents;

/// <summary>
/// Read-only telemetry tools exposed to the model. Design rules (see CLAUDE.md, "Tool design"):
///  - every tool is bounded in time and size;
///  - outputs are summaries, never raw dumps;
///  - descriptions are written for the model: say what it returns and when to use it;
///  - read-only tools need no approval. Anything that changes the system lives elsewhere and requires approval.
/// In Phase 2 these move behind an MCP server; the descriptions travel with them.
/// </summary>
public sealed class TelemetryTools(ITelemetryQuery telemetry, TimeProvider time)
{
    public const string GetErrorLogsName = "get_error_logs";
    public const string GetMetricName = "get_metric";
    public const string GetTraceName = "get_trace";

    private const int MaxLookbackMinutes = 120;

    [Description("Top error log groups for a service: messages grouped and counted, with first/last seen and one sample trace id per group. Use it first to see WHAT is failing.")]
    public Task<LogSummary> GetErrorLogsAsync(
        [Description("Service name, e.g. 'orders-api' or 'catalog-api'")] string service,
        [Description("Look-back window in minutes (1-120). Default 15.")] int minutes = 15,
        CancellationToken cancellationToken = default) =>
        telemetry.QueryLogsAsync(new LogQuery(service, Window(minutes)), cancellationToken);

    [Description("A metric for a service over time: min/max/avg/last plus ~1 point per minute. Metrics: error_rate, p95_latency_ms, request_rate. Use it to see WHEN it started and HOW BAD it is.")]
    public Task<MetricSummary> GetMetricAsync(
        [Description("Service name")] string service,
        [Description("One of: error_rate, p95_latency_ms, request_rate")] string metric,
        [Description("Look-back window in minutes (1-120). Default 30.")] int minutes = 30,
        CancellationToken cancellationToken = default)
    {
        if (!Metrics.All.Contains(metric))
        {
            throw new ArgumentException($"Unknown metric '{metric}'. Use one of: {string.Join(", ", Metrics.All)}.", nameof(metric));
        }

        return telemetry.QueryMetricAsync(new MetricQuery(service, metric, Window(minutes)), cancellationToken);
    }

    [Description("Summary of one distributed trace: total duration, the 5 slowest spans and every span with an error. Use it with a sample trace id from get_error_logs to see WHERE in the call chain it fails.")]
    public Task<TraceSummary?> GetTraceAsync(
        [Description("Trace id (32 hex chars)")] string traceId,
        CancellationToken cancellationToken = default) =>
        telemetry.GetTraceAsync(traceId, cancellationToken);

    public IList<AITool> AsAITools() =>
    [
        AIFunctionFactory.Create(GetErrorLogsAsync, name: GetErrorLogsName),
        AIFunctionFactory.Create(GetMetricAsync, name: GetMetricName),
        AIFunctionFactory.Create(GetTraceAsync, name: GetTraceName),
    ];

    private TimeWindow Window(int minutes) => TimeWindow.LastMinutes(time, Math.Clamp(minutes, 1, MaxLookbackMinutes));
}
