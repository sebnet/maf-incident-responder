using IncidentResponder.Agents;
using IncidentResponder.Core;
using IncidentResponder.Evals;
using IncidentResponder.Tests.Fakes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IncidentResponder.Tests;

/// <summary>DiagnosisRun.RunAsync: one eval run with a fresh agent whose tools are bounded by the run's not-before time.</summary>
public sealed class DiagnosisRunTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private const string DiagnosisJson = """
        { "rootCauseService": "catalog-api", "category": "ApplicationError", "rootCause": "NaN prices", "confidence": 0.9,
          "evidence": [ { "source": "logs:catalog-api", "summary": "InvalidOperationException" } ], "suggestedActions": [] }
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (EvalContext Context, FakeChatClient Chat, StubTelemetryQuery Telemetry) CreateContext()
    {
        var chat = new FakeChatClient();
        var telemetry = new StubTelemetryQuery();
        var context = new EvalContext(chat, telemetry, new FixedTimeProvider(Now), Options.Create(new BudgetOptions()), NullLoggerFactory.Instance);
        return (context, chat, telemetry);
    }

    private static Alert AlertFor(string service) => new(service, "error_rate", $"{service} error rate above 5%", Now);

    [Fact]
    public async Task Diagnoses_with_tools_that_never_look_before_the_runs_not_before_time()
    {
        var (context, chat, telemetry) = CreateContext();
        chat.EnqueueToolCall("c1", TelemetryTools.GetErrorLogsName, new Dictionary<string, object?> { ["service"] = "orders-api", ["minutes"] = 60 })
            .EnqueueText(DiagnosisJson);

        var response = await DiagnosisRun.RunAsync(context, AlertFor("orders-api"), notBefore: Now.AddMinutes(-7), Ct);

        Assert.Equal(new TimeWindow(Now.AddMinutes(-7), Now), Assert.Single(telemetry.LogQueries).Window); // asked 60, got 7
        Assert.Equal("catalog-api", response.Result.RootCauseService);
    }

    [Fact]
    public async Task Each_run_starts_with_no_history_of_the_previous_one()
    {
        var (context, chat, _) = CreateContext();
        chat.EnqueueText(DiagnosisJson).EnqueueText(DiagnosisJson);

        await DiagnosisRun.RunAsync(context, AlertFor("orders-api"), Now.AddMinutes(-5), Ct);
        await DiagnosisRun.RunAsync(context, AlertFor("catalog-api"), Now.AddMinutes(-5), Ct);

        Assert.DoesNotContain(chat.Requests[1], m => m.Text.Contains("orders-api", StringComparison.Ordinal));
        Assert.DoesNotContain(chat.Requests[1], m => m.Role == ChatRole.Assistant);
    }

    [Fact]
    public async Task The_response_carries_what_the_runner_measures()
    {
        var (context, chat, _) = CreateContext();
        chat.Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("c1", TelemetryTools.GetMetricName, new Dictionary<string, object?> { ["service"] = "orders-api", ["metric"] = Metrics.ErrorRate })]))
            { Usage = new UsageDetails { InputTokenCount = 1000, OutputTokenCount = 20 } })
            .Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant, DiagnosisJson))
            { Usage = new UsageDetails { InputTokenCount = 2500, OutputTokenCount = 150 } });

        var response = await DiagnosisRun.RunAsync(context, AlertFor("orders-api"), Now.AddMinutes(-5), Ct);
        var metrics = RunMetrics.From(response);

        Assert.Equal(3500, metrics.InputTokens);
        Assert.Equal(2, metrics.RoundTrips);
        Assert.Equal([TelemetryTools.GetMetricName], metrics.ToolsUsed);
    }
}
