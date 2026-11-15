using IncidentResponder.Agents;
using IncidentResponder.Core;
using IncidentResponder.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IncidentResponder.Tests;

public sealed class OnCallAssistantTests
{
    [Fact]
    public async Task Calls_the_error_logs_tool_the_model_asks_for_and_returns_the_final_answer()
    {
        // Arrange: the "model" first asks for error logs of orders-api, then answers.
        var telemetry = new StubTelemetryQuery();
        var chat = new FakeChatClient()
            .EnqueueToolCall("call-1", TelemetryTools.GetErrorLogsName, new Dictionary<string, object?>
            {
                ["service"] = "orders-api",
                ["minutes"] = 15,
            })
            .EnqueueText("Root cause: catalog-api is returning 500s.");

        var agent = OnCallAssistant.Create(
            chat,
            new TelemetryTools(telemetry, TimeProvider.System),
            new ToolCallMiddleware(NullLogger<ToolCallMiddleware>.Instance, Options.Create(new BudgetOptions())));

        // Act
        var response = await agent.RunAsync("what's wrong with orders-api?", cancellationToken: TestContext.Current.CancellationToken);

        // Assert: the tool really ran with the model's arguments, and the model was called twice
        // (once to decide the tool call, once with the tool result to answer).
        var query = Assert.Single(telemetry.LogQueries);
        Assert.Equal("orders-api", query.Service);
        Assert.Equal(2, chat.Requests.Count);
        Assert.Contains("catalog-api", response.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_metric_rejects_unknown_metric_names()
    {
        var tools = new TelemetryTools(new StubTelemetryQuery(), TimeProvider.System);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            tools.GetMetricAsync("orders-api", "cpu_magic", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Look_back_window_is_clamped_to_two_hours()
    {
        var telemetry = new StubTelemetryQuery();
        var tools = new TelemetryTools(telemetry, TimeProvider.System);

        await tools.GetErrorLogsAsync("orders-api", minutes: 10_000, cancellationToken: TestContext.Current.CancellationToken);

        var window = Assert.Single(telemetry.LogQueries).Window;
        Assert.Equal(TimeSpan.FromMinutes(120), window.To - window.From);
    }
}
