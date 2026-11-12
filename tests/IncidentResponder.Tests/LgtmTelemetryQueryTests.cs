using System.Net;
using System.Web;
using IncidentResponder.Core;
using IncidentResponder.Telemetry;
using IncidentResponder.Tests.Fakes;

namespace IncidentResponder.Tests;

/// <summary>
/// LgtmTelemetryQuery against responses recorded from the real LGTM while s01 was active
/// (catalog-api failing 60 % of item lookups). Fixtures: Fixtures/Lgtm/*.json.
/// </summary>
public sealed class LgtmTelemetryQueryTests
{
    // The three minutes the fixtures were recorded in.
    private static readonly TimeWindow S01Window = new(
        DateTimeOffset.FromUnixTimeSeconds(1794488583),
        DateTimeOffset.FromUnixTimeSeconds(1794488763));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LgtmTelemetryQuery Create(FakeHttpMessageHandler handler) =>
        new(new HttpClient(handler), LgtmOptions.LocalDefaults);

    private static string QueryParam(Uri uri, string name) => HttpUtility.ParseQueryString(uri.Query)[name]!;

    // ---------- Loki ----------

    [Fact]
    public async Task Groups_error_logs_by_message_template_and_counts_them()
    {
        var telemetry = Create(FakeHttpMessageHandler.FromFixture("loki-orders-api-errors-s01.json"));

        var summary = await telemetry.QueryLogsAsync(new LogQuery("orders-api", S01Window), Ct);

        // 14 lines, but only two distinct errors: the unhandled exception and Polly giving up.
        Assert.Equal(14, summary.TotalMatches);
        Assert.Equal(2, summary.TopGroups.Count);
        Assert.All(summary.TopGroups, g => Assert.Equal(7, g.Count));
        Assert.All(summary.TopGroups, g => Assert.True(g.FirstSeen <= g.LastSeen));
        Assert.All(summary.TopGroups, g => Assert.Matches("^[0-9a-f]{32}$", g.SampleTraceId!));
    }

    [Fact]
    public async Task Error_groups_carry_the_exception_and_the_failing_dependency_but_not_the_stack_trace()
    {
        var telemetry = Create(FakeHttpMessageHandler.FromFixture("loki-orders-api-errors-s01.json"));

        var summary = await telemetry.QueryLogsAsync(new LogQuery("orders-api", S01Window), Ct);

        var exception = Assert.Single(summary.TopGroups, g => g.Message.Contains("unhandled exception", StringComparison.Ordinal));
        Assert.Contains("HttpRequestException: Response status code does not indicate success: 500", exception.Message, StringComparison.Ordinal);
        Assert.Contains("request: /orders", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", exception.Message, StringComparison.Ordinal);

        // Polly's line is the one that names the dependency; item ids are normalized so all retries group together.
        var retry = Assert.Single(summary.TopGroups, g => g.Message.StartsWith("Execution attempt.", StringComparison.Ordinal));
        Assert.Contains("dependency: https+http://catalog-api/items/{n}", retry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Surfaces_the_root_cause_exception_message_of_the_failing_service()
    {
        var telemetry = Create(FakeHttpMessageHandler.FromFixture("loki-catalog-api-errors-s01.json"));

        var summary = await telemetry.QueryLogsAsync(new LogQuery("catalog-api", S01Window), Ct);

        var group = Assert.Single(summary.TopGroups);
        Assert.Equal(4, group.Count);
        Assert.Contains("InvalidOperationException: Cannot read item price: value 'NaN' is not a valid decimal", group.Message, StringComparison.Ordinal);
        Assert.Contains("request: /items/{n}", group.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Builds_a_bounded_LogQL_query_for_the_service_and_minimum_level()
    {
        var handler = FakeHttpMessageHandler.FromFixture("loki-orders-api-errors-s01.json");
        var telemetry = Create(handler);

        await telemetry.QueryLogsAsync(new LogQuery("orders-api", S01Window, MinLevel: "Warning", Contains: "say \"hi\""), Ct);

        var uri = Assert.Single(handler.Requests);
        Assert.Equal("/loki/api/v1/query_range", uri.AbsolutePath);
        Assert.Equal(
            """{service_name="orders-api"} | detected_level=~"warn|error|critical|fatal" |= "say \"hi\"" """.TrimEnd(),
            QueryParam(uri, "query"));
        Assert.Equal("1794488583000000000", QueryParam(uri, "start"));
        Assert.Equal("1794488763000000000", QueryParam(uri, "end"));
        Assert.Equal("5000", QueryParam(uri, "limit"));
        Assert.Equal("backward", QueryParam(uri, "direction"));
    }

    [Fact]
    public async Task Keeps_only_the_top_N_groups_most_frequent_first()
    {
        const string Body = """
            {"status":"success","data":{"resultType":"streams","result":[
              {"stream":{"_OriginalFormat_":"A {Id}","detected_level":"error"},"values":[["1794488600000000000","A 1"],["1794488601000000000","A 2"]]},
              {"stream":{"_OriginalFormat_":"B","detected_level":"error"},"values":[["1794488602000000000","B"]]},
              {"stream":{"_OriginalFormat_":"C","detected_level":"error"},"values":[["1794488603000000000","C"],["1794488604000000000","C"],["1794488605000000000","C"]]}
            ]}}
            """;
        var telemetry = Create(new FakeHttpMessageHandler(HttpStatusCode.OK, Body));

        var summary = await telemetry.QueryLogsAsync(new LogQuery("orders-api", S01Window, Top: 2), Ct);

        Assert.Equal(6, summary.TotalMatches);
        Assert.Equal(["C", "A 2"], summary.TopGroups.Select(g => g.Message));
        Assert.Equal([3, 2], summary.TopGroups.Select(g => g.Count));
    }

    [Fact]
    public async Task Rejects_an_unknown_log_level_without_calling_Loki()
    {
        var handler = FakeHttpMessageHandler.FromFixture("loki-orders-api-errors-s01.json");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Create(handler).QueryLogsAsync(new LogQuery("orders-api", S01Window, MinLevel: "panic"), Ct));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Backend_errors_include_the_response_body()
    {
        var telemetry = Create(new FakeHttpMessageHandler(HttpStatusCode.BadRequest, "parse error at line 1, col 18: syntax error"));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            telemetry.QueryLogsAsync(new LogQuery("orders-api", S01Window), Ct));

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Contains("Loki returned 400", ex.Message, StringComparison.Ordinal);
        Assert.Contains("parse error", ex.Message, StringComparison.Ordinal);
    }

    // ---------- Prometheus ----------

    [Fact]
    public async Task Summarizes_p95_latency_in_milliseconds()
    {
        var telemetry = Create(FakeHttpMessageHandler.FromFixture("prom-orders-api-p95-s01.json"));

        var summary = await telemetry.QueryMetricAsync(new MetricQuery("orders-api", Metrics.P95LatencyMs, S01Window), Ct);

        Assert.Equal([24.5, 24.2, 4765.2], summary.Series.Select(p => p.Value));
        Assert.Equal(24.2, summary.Min);
        Assert.Equal(4765.2, summary.Max);
        Assert.Equal(1604.6, summary.Avg);
        Assert.Equal(4765.2, summary.Last);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794488763), summary.Series[^1].At);
    }

    [Fact]
    public async Task Error_rate_is_5xx_over_all_requests_of_the_service_at_one_point_per_minute()
    {
        var handler = FakeHttpMessageHandler.FromFixture("prom-orders-api-error-rate-s01.json");

        var summary = await Create(handler).QueryMetricAsync(new MetricQuery("orders-api", Metrics.ErrorRate, S01Window), Ct);

        var uri = Assert.Single(handler.Requests);
        Assert.Equal("/api/v1/query_range", uri.AbsolutePath);
        Assert.Equal(
            """(sum(rate(http_server_request_duration_seconds_count{service_name="orders-api",http_response_status_code=~"5.."}[120s])) or vector(0)) / sum(rate(http_server_request_duration_seconds_count{service_name="orders-api"}[120s]))""",
            QueryParam(uri, "query"));
        Assert.Equal("60", QueryParam(uri, "step"));
        Assert.Equal([0, 0.0048, 0.0478], summary.Series.Select(p => p.Value));
    }

    [Fact]
    public async Task Long_windows_are_downsampled_to_at_most_30_points()
    {
        var handler = FakeHttpMessageHandler.FromFixture("prom-empty.json");
        var twoHours = new TimeWindow(S01Window.To.AddHours(-2), S01Window.To);

        await Create(handler).QueryMetricAsync(new MetricQuery("orders-api", Metrics.RequestRate, twoHours), Ct);

        var uri = Assert.Single(handler.Requests);
        Assert.Equal("240", QueryParam(uri, "step"));
        Assert.Contains("[240s]", QueryParam(uri, "query"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_data_gives_zeros_and_an_empty_series()
    {
        var telemetry = Create(FakeHttpMessageHandler.FromFixture("prom-empty.json"));

        var summary = await telemetry.QueryMetricAsync(new MetricQuery("nope", Metrics.RequestRate, S01Window), Ct);

        Assert.Empty(summary.Series);
        Assert.Equal(0, summary.Max);
    }

    [Fact]
    public async Task Steps_without_traffic_NaN_are_dropped()
    {
        const string Body = """
            {"status":"success","data":{"resultType":"matrix","result":[{"metric":{},"values":[[1794488643,"NaN"],[1794488703,"12.34"],[1794488763,"+Inf"]]}]}}
            """;
        var telemetry = Create(new FakeHttpMessageHandler(HttpStatusCode.OK, Body));

        var summary = await telemetry.QueryMetricAsync(new MetricQuery("orders-api", Metrics.P95LatencyMs, S01Window), Ct);

        var point = Assert.Single(summary.Series);
        Assert.Equal(12.3, point.Value);
    }

    // ---------- Tempo ----------

    [Fact]
    public async Task Summarizes_a_trace_with_its_slowest_spans_and_its_error_spans()
    {
        var handler = FakeHttpMessageHandler.FromFixture("tempo-trace-s01.json");
        const string TraceId = "29a128905fc2a3e1dd6c78b73ce81fb0";

        var summary = await Create(handler).GetTraceAsync(TraceId, Ct);

        Assert.NotNull(summary);
        Assert.Equal("/api/traces/" + TraceId, Assert.Single(handler.Requests).AbsolutePath);
        Assert.Equal(TraceId, summary.TraceId);
        Assert.Equal(TimeSpan.FromTicks(88_905_329), summary.Duration); // loadgen's POST, the root span

        Assert.Equal(5, summary.SlowestSpans.Count);
        Assert.Equal(("loadgen", "POST /orders -> orders-api"), (summary.SlowestSpans[0].Service, summary.SlowestSpans[0].Name));
        Assert.Equal(("orders-api", "POST /orders"), (summary.SlowestSpans[1].Service, summary.SlowestSpans[1].Name));

        // Every span in this trace failed: 1 loadgen + 1 orders server + 4 orders->catalog attempts + 4 catalog server.
        Assert.Equal(10, summary.ErrorSpans.Count);
        Assert.Equal(4, summary.ErrorSpans.Count(s => s is { Service: "orders-api", Name: "GET /items/3 -> catalog-api", Error: "HTTP 500" }));
        Assert.Equal(4, summary.ErrorSpans.Count(s => s is { Service: "catalog-api", Name: "GET /items/{id:int}", Error: "System.InvalidOperationException, HTTP 500" }));
    }

    [Fact]
    public async Task Unknown_trace_returns_null()
    {
        var telemetry = Create(new FakeHttpMessageHandler(HttpStatusCode.NotFound, string.Empty));

        Assert.Null(await telemetry.GetTraceAsync("0123456789abcdef0123456789abcdef", Ct));
    }

    [Theory]
    [InlineData("not-a-trace-id")]
    [InlineData("../../api/search")]
    [InlineData("29a128905fc2a3e1dd6c78b73ce81fb")] // 31 chars
    public async Task Rejects_malformed_trace_ids_without_calling_Tempo(string traceId)
    {
        var handler = FakeHttpMessageHandler.FromFixture("tempo-trace-s01.json");

        await Assert.ThrowsAsync<ArgumentException>(() => Create(handler).GetTraceAsync(traceId, Ct));
        Assert.Empty(handler.Requests);
    }
}
