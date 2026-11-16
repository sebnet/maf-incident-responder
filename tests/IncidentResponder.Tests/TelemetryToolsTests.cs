using IncidentResponder.Agents;
using IncidentResponder.Core;
using IncidentResponder.Tests.Fakes;

namespace IncidentResponder.Tests;

/// <summary>The optional "not before" bound the eval runner uses to keep runs from seeing each other's telemetry.</summary>
public sealed class TelemetryToolsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_not_before_time_bounds_the_look_back_window_whatever_the_model_asks_for()
    {
        var telemetry = new StubTelemetryQuery();
        var tools = new TelemetryTools(telemetry, new FixedTimeProvider(Now), notBefore: Now.AddMinutes(-10));

        await tools.GetErrorLogsAsync("orders-api", minutes: 60, cancellationToken: Ct);
        await tools.GetMetricAsync("orders-api", Metrics.ErrorRate, minutes: 120, cancellationToken: Ct);

        Assert.Equal(new TimeWindow(Now.AddMinutes(-10), Now), Assert.Single(telemetry.LogQueries).Window);
        Assert.Equal(new TimeWindow(Now.AddMinutes(-10), Now), Assert.Single(telemetry.MetricQueries).Window);
    }

    [Fact]
    public async Task Windows_inside_the_bound_are_left_as_asked()
    {
        var telemetry = new StubTelemetryQuery();
        var tools = new TelemetryTools(telemetry, new FixedTimeProvider(Now), notBefore: Now.AddMinutes(-10));

        await tools.GetErrorLogsAsync("orders-api", minutes: 5, cancellationToken: Ct);

        Assert.Equal(new TimeWindow(Now.AddMinutes(-5), Now), Assert.Single(telemetry.LogQueries).Window);
    }

    [Fact]
    public async Task A_not_before_time_in_the_future_gives_an_empty_window_not_an_invalid_one()
    {
        var telemetry = new StubTelemetryQuery();
        var tools = new TelemetryTools(telemetry, new FixedTimeProvider(Now), notBefore: Now.AddMinutes(5));

        await tools.GetErrorLogsAsync("orders-api", minutes: 15, cancellationToken: Ct);

        Assert.Equal(new TimeWindow(Now, Now), Assert.Single(telemetry.LogQueries).Window);
    }

    [Fact]
    public async Task Without_a_bound_only_the_two_hour_limit_applies()
    {
        var telemetry = new StubTelemetryQuery();
        var tools = new TelemetryTools(telemetry, new FixedTimeProvider(Now));

        await tools.GetErrorLogsAsync("orders-api", minutes: 90, cancellationToken: Ct);

        Assert.Equal(new TimeWindow(Now.AddMinutes(-90), Now), Assert.Single(telemetry.LogQueries).Window);
    }
}
