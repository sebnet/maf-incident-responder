using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
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
/// <see cref="ITelemetryQuery"/> over Grafana LGTM, talking directly to the backends' HTTP APIs:
///  - logs    → Loki       GET /loki/api/v1/query_range  (<see cref="LokiLogs"/>)
///  - metrics → Prometheus GET /api/v1/query_range       (<see cref="PrometheusMetrics"/>)
///  - traces  → Tempo      GET /api/traces/{traceId}     (<see cref="TempoTraces"/>)
/// This class only does HTTP; building queries and summarizing responses lives in those helpers.
/// Labels and response shapes were checked against grafana/otel-lgtm 0.40.0: docs/notes/lgtm-labels.md.
/// </summary>
public sealed class LgtmTelemetryQuery(HttpClient http, LgtmOptions options) : ITelemetryQuery
{
    private const int MaxErrorBodyLength = 500;

    public async Task<LogSummary> QueryLogsAsync(LogQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var url = Combine(options.Loki, "loki/api/v1/query_range", new()
        {
            ["query"] = LokiLogs.BuildQuery(query),
            ["start"] = LokiLogs.ToUnixNanos(query.Window.From).ToString(CultureInfo.InvariantCulture),
            ["end"] = LokiLogs.ToUnixNanos(query.Window.To).ToString(CultureInfo.InvariantCulture),
            ["limit"] = LokiLogs.MaxLines.ToString(CultureInfo.InvariantCulture),
            ["direction"] = "backward",
        });

        using var json = await GetJsonAsync(url, "Loki", cancellationToken).ConfigureAwait(false);
        return LokiLogs.Summarize(json!.RootElement, query);
    }

    public async Task<MetricSummary> QueryMetricAsync(MetricQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var step = PrometheusMetrics.Step(query.Window);
        var url = Combine(options.Prometheus, "api/v1/query_range", new()
        {
            ["query"] = PrometheusMetrics.BuildQuery(query, step),
            ["start"] = query.Window.From.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ["end"] = query.Window.To.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ["step"] = ((int)step.TotalSeconds).ToString(CultureInfo.InvariantCulture),
        });

        using var json = await GetJsonAsync(url, "Prometheus", cancellationToken).ConfigureAwait(false);
        return PrometheusMetrics.Summarize(json!.RootElement, query);
    }

    public async Task<TraceSummary?> GetTraceAsync(string traceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(traceId);

        traceId = traceId.Trim();
        if (!TempoTraces.IsValidTraceId(traceId))
        {
            // Checked before calling Tempo: the id comes from the model and must not be able to alter the URL.
            throw new ArgumentException($"'{traceId}' is not a trace id: expected 32 hex characters.", nameof(traceId));
        }

        var url = Combine(options.Tempo, $"api/traces/{traceId}", new());
        using var json = await GetJsonAsync(url, "Tempo", cancellationToken, notFoundIsNull: true).ConfigureAwait(false);
        return json is null ? null : TempoTraces.Summarize(traceId, json.RootElement);
    }

    private async Task<JsonDocument?> GetJsonAsync(Uri url, string backend, CancellationToken cancellationToken, bool notFoundIsNull = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // Tempo can also answer protobuf: ask for JSON explicitly.
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (notFoundIsNull && response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            // Loki and Prometheus explain bad queries in the body: keep it, it is what makes the error actionable.
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (body.Length > MaxErrorBodyLength)
            {
                body = body[..MaxErrorBodyLength] + "…";
            }

            throw new HttpRequestException($"{backend} returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}", null, response.StatusCode);
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private static Uri Combine(Uri baseUri, string path, Dictionary<string, string> queryString)
    {
        var query = string.Join('&', queryString.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        var builder = new UriBuilder(baseUri)
        {
            Path = baseUri.AbsolutePath.TrimEnd('/') + "/" + path,
            Query = query,
        };
        return builder.Uri;
    }
}
