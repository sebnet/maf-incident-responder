using IncidentResponder.Agents;
using IncidentResponder.Evals;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace IncidentResponder.Tests;

/// <summary>RunMetrics.From: what the eval runner reads from a run's AgentResponse besides the diagnosis.</summary>
public sealed class RunMetricsTests
{
    private static FunctionCallContent Call(string id, string tool) => new(id, tool, new Dictionary<string, object?> { ["service"] = "orders-api" });

    [Fact]
    public void Counts_round_trips_and_tool_calls_with_parallel_calls_in_one_response()
    {
        // Round trip 1: two tools at once. Round trip 2: one tool. Round trip 3: the answer.
        var response = new AgentResponse(
        [
            new ChatMessage(ChatRole.Assistant, [Call("c1", TelemetryTools.GetErrorLogsName), Call("c2", TelemetryTools.GetMetricName)]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "logs"), new FunctionResultContent("c2", "metric")]),
            new ChatMessage(ChatRole.Assistant, [Call("c3", TelemetryTools.GetTraceName)]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c3", "trace")]),
            new ChatMessage(ChatRole.Assistant, "the diagnosis"),
        ]);

        var metrics = RunMetrics.From(response);

        Assert.Equal(3, metrics.RoundTrips);
        Assert.Equal(3, metrics.ToolCalls);
        Assert.Equal([TelemetryTools.GetErrorLogsName, TelemetryTools.GetMetricName, TelemetryTools.GetTraceName], metrics.ToolsUsed);
        Assert.Equal(0, metrics.BudgetHits);
        Assert.Equal(0, metrics.FailedToolCalls);
    }

    [Fact]
    public void Reads_the_token_counts_of_the_whole_run()
    {
        var response = new AgentResponse([new ChatMessage(ChatRole.Assistant, "the diagnosis")])
        {
            Usage = new UsageDetails { InputTokenCount = 9000, CachedInputTokenCount = 3000, OutputTokenCount = 400, ReasoningTokenCount = 100 },
        };

        var metrics = RunMetrics.From(response);

        Assert.Equal(9000, metrics.InputTokens);
        Assert.Equal(3000, metrics.CachedInputTokens);
        Assert.Equal(400, metrics.OutputTokens);
        Assert.Equal(100, metrics.ReasoningTokens);
    }

    [Fact]
    public void Unreported_tokens_are_unknown_not_zero()
    {
        var metrics = RunMetrics.From(new AgentResponse([new ChatMessage(ChatRole.Assistant, "the diagnosis")]));

        Assert.Null(metrics.InputTokens);
        Assert.Null(metrics.OutputTokens);
    }

    [Fact]
    public void A_count_the_provider_leaves_out_stays_unknown_even_when_others_are_reported()
    {
        // Models without reasoning report input and output but no reasoning count: that is "unknown", not "none".
        var response = new AgentResponse([new ChatMessage(ChatRole.Assistant, "the diagnosis")])
        {
            Usage = new UsageDetails { InputTokenCount = 9000, OutputTokenCount = 400 },
        };

        var metrics = RunMetrics.From(response);

        Assert.Null(metrics.ReasoningTokens);
        Assert.Null(metrics.CachedInputTokens);
    }

    [Fact]
    public void Counts_budget_refusals_and_failed_tools()
    {
        var response = new AgentResponse(
        [
            new ChatMessage(ChatRole.Assistant, [Call("c1", TelemetryTools.GetTraceName), Call("c2", TelemetryTools.GetMetricName)]),
            new ChatMessage(ChatRole.Tool,
            [
                new FunctionResultContent("c1", "Error: Function failed.") { Exception = new ArgumentException("bad trace id") },
                new FunctionResultContent("c2", $"{ToolCallMiddleware.BudgetExhaustedMessage} (10 round trips with tools). This tool was not run."),
            ]),
            new ChatMessage(ChatRole.Assistant, "the diagnosis"),
        ]);

        var metrics = RunMetrics.From(response);

        Assert.Equal(2, metrics.ToolCalls); // refused and failed calls were still asked for
        Assert.Equal(1, metrics.FailedToolCalls);
        Assert.Equal(1, metrics.BudgetHits);
    }
}
