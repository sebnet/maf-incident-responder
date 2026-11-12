using System.Globalization;
using System.Text.Json;
using IncidentResponder.Core;

namespace IncidentResponder.Telemetry;

/// <summary>
/// Prometheus side of <see cref="LgtmTelemetryQuery"/>: maps the tool's metric names to PromQL over the OTel HTTP
/// server histogram and downsamples the result. See docs/notes/lgtm-labels.md.
/// </summary>
internal static class PrometheusMetrics
{
    /// <summary>A series never has more points than this; the step grows with the window instead.</summary>
    public const int MaxPoints = 30;

    // rate() needs at least two samples in its window; 2 minutes is safe with the default export interval.
    private static readonly TimeSpan MinRateWindow = TimeSpan.FromMinutes(2);

    /// <summary>~1 point per minute, coarser for long windows so the series stays at most <see cref="MaxPoints"/>.</summary>
    public static TimeSpan Step(TimeWindow window)
    {
        var minutes = Math.Ceiling((window.To - window.From).TotalMinutes / MaxPoints);
        return TimeSpan.FromMinutes(Math.Max(1, minutes));
    }

    /// <summary>
    /// error_rate counts 5xx only: 499s (client gave up) are not server errors, see lgtm-labels.md.
    /// The <c>or vector(0)</c> keeps the series at 0 when there are requests but no 5xx (the numerator would
    /// otherwise be empty); with no requests at all there is no point, which is the honest answer.
    /// </summary>
    public static string BuildQuery(MetricQuery query, TimeSpan step)
    {
        var selector = $"service_name=\"{Escape(query.Service)}\"";
        var range = $"[{(int)Max(step, MinRateWindow).TotalSeconds}s]";
        const string Duration = "http_server_request_duration_seconds";

        return query.Metric switch
        {
            Metrics.ErrorRate =>
                $"(sum(rate({Duration}_count{{{selector},http_response_status_code=~\"5..\"}}{range})) or vector(0))" +
                $" / sum(rate({Duration}_count{{{selector}}}{range}))",
            Metrics.P95LatencyMs =>
                $"1000 * histogram_quantile(0.95, sum by (le) (rate({Duration}_bucket{{{selector}}}{range})))",
            Metrics.RequestRate =>
                $"sum(rate({Duration}_count{{{selector}}}{range}))",
            _ => throw new ArgumentException(
                $"Unknown metric '{query.Metric}'. Use one of: {string.Join(", ", Metrics.All)}.", nameof(query)),
        };
    }

    public static MetricSummary Summarize(JsonElement root, MetricQuery query)
    {
        var points = new List<MetricPoint>();
        var result = root.GetProperty("data").GetProperty("result");

        // Every query aggregates with sum(...), so there is at most one series.
        if (result.GetArrayLength() > 0)
        {
            foreach (var pair in result[0].GetProperty("values").EnumerateArray())
            {
                // Prometheus writes NaN, +Inf and -Inf; .NET does not parse "+Inf". NaN shows up when the step had no
                // traffic (histogram_quantile, 0/0). Non-finite points are dropped.
                if (double.TryParse(pair[1].GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    && double.IsFinite(value))
                {
                    var at = DateTimeOffset.FromUnixTimeMilliseconds((long)(pair[0].GetDouble() * 1000));
                    points.Add(new MetricPoint(at, Round(query.Metric, value)));
                }
            }
        }

        if (points.Count == 0)
        {
            // No data: zeros plus an empty series. NaN would not serialize to JSON for the model.
            return new MetricSummary(query.Service, query.Metric, query.Window, 0, 0, 0, 0, []);
        }

        return new MetricSummary(
            query.Service,
            query.Metric,
            query.Window,
            Min: points.Min(p => p.Value),
            Max: points.Max(p => p.Value),
            Avg: Round(query.Metric, points.Average(p => p.Value)),
            Last: points[^1].Value,
            Series: points);
    }

    /// <summary>Fewer digits = fewer tokens. Milliseconds to 0.1, ratios and req/s to 4 decimals.</summary>
    private static double Round(string metric, double value) =>
        Math.Round(value, metric == Metrics.P95LatencyMs ? 1 : 4, MidpointRounding.AwayFromZero);

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
