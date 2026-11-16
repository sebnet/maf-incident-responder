using System.Text.Json;
using IncidentResponder.Agents;
using IncidentResponder.Core;
using IncidentResponder.Tests.Fakes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IncidentResponder.Tests;

/// <summary>
/// OnCallAssistant.DiagnoseAsync: structured output with RunAsync&lt;Diagnosis&gt;. The fake model scripts what a real
/// one would do (tool calls, then a JSON answer that follows the schema); the tests check what the agent does with it.
/// </summary>
public sealed class DiagnoseTests
{
    private static readonly Alert OrdersErrors = new(
        "orders-api", "error_rate", "orders-api error rate above 5% for 2 minutes", new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero));

    private static readonly Alert CatalogLatency = new(
        "catalog-api", "p95_latency_ms", "catalog-api p95 latency above 1500 ms", new DateTimeOffset(2026, 11, 13, 11, 0, 0, TimeSpan.Zero));

    // What a model answers when it follows the schema: camelCase names, enum as a string.
    private const string DiagnosisJson = """
        {
          "rootCauseService": "catalog-api",
          "category": "ApplicationError",
          "rootCause": "catalog-api throws InvalidOperationException reading item prices ('NaN').",
          "confidence": 0.9,
          "evidence": [ { "source": "logs:catalog-api", "summary": "155 InvalidOperationException: Cannot read item price" } ],
          "suggestedActions": [ "Repair the catalog data" ]
        }
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (AIAgent Agent, StubTelemetryQuery Telemetry) CreateAgent(FakeChatClient chat)
    {
        var telemetry = new StubTelemetryQuery();
        var agent = OnCallAssistant.Create(
            chat,
            new TelemetryTools(telemetry, TimeProvider.System),
            new ToolCallMiddleware(NullLogger<ToolCallMiddleware>.Instance, Options.Create(new BudgetOptions())));
        return (agent, telemetry);
    }

    [Fact]
    public async Task Returns_the_typed_diagnosis_the_model_produces_after_using_tools()
    {
        var chat = new FakeChatClient()
            .EnqueueToolCall("call-1", TelemetryTools.GetErrorLogsName, new Dictionary<string, object?> { ["service"] = "catalog-api" })
            .EnqueueText(DiagnosisJson);
        var (agent, telemetry) = CreateAgent(chat);

        var diagnosis = (await OnCallAssistant.DiagnoseAsync(agent, OrdersErrors, Ct)).Result;

        Assert.Equal("catalog-api", diagnosis.RootCauseService);
        Assert.Equal(RootCauseCategory.ApplicationError, diagnosis.Category);
        Assert.Equal(0.9, diagnosis.Confidence);
        Assert.Equal("logs:catalog-api", Assert.Single(diagnosis.Evidence).Source);
        Assert.Equal("catalog-api", Assert.Single(telemetry.LogQueries).Service); // tools still work with structured output
    }

    [Fact]
    public async Task Sends_the_alert_and_the_Diagnosis_schema_to_the_model()
    {
        var chat = new FakeChatClient().EnqueueText(DiagnosisJson);
        var (agent, _) = CreateAgent(chat);

        await OnCallAssistant.DiagnoseAsync(agent, OrdersErrors, Ct);

        var userMessage = Assert.Single(chat.Requests[0], m => m.Role == ChatRole.User);
        Assert.Equal(OnCallAssistant.DiagnoseRequest(OrdersErrors), userMessage.Text);

        // The schema is what forces the shape. Category names are in it, as strings.
        var format = Assert.IsType<ChatResponseFormatJson>(chat.Options[^1]?.ResponseFormat);
        var schema = format.Schema!.Value.GetRawText();
        Assert.Contains("rootCauseService", schema, StringComparison.Ordinal);
        Assert.Contains("ApplicationError", schema, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_diagnosis_starts_from_a_fresh_session()
    {
        var chat = new FakeChatClient().EnqueueText(DiagnosisJson).EnqueueText(DiagnosisJson);
        var (agent, _) = CreateAgent(chat);

        await OnCallAssistant.DiagnoseAsync(agent, OrdersErrors, Ct);
        await OnCallAssistant.DiagnoseAsync(agent, CatalogLatency, Ct);

        // The second run must not carry the first alert (or its answer) in its history.
        Assert.DoesNotContain(chat.Requests[1], m => m.Text.Contains(OrdersErrors.Description, StringComparison.Ordinal));
        Assert.DoesNotContain(chat.Requests[1], m => m.Role == ChatRole.Assistant);
    }

    [Fact]
    public async Task The_schema_explains_the_categories_on_the_property_because_enum_value_descriptions_are_dropped()
    {
        var chat = new FakeChatClient().EnqueueText(DiagnosisJson);
        var (agent, _) = CreateAgent(chat);

        await OnCallAssistant.DiagnoseAsync(agent, OrdersErrors, Ct);

        var schema = Assert.IsType<ChatResponseFormatJson>(chat.Options[^1]?.ResponseFormat).Schema!.Value;
        var category = schema.GetProperty("properties").GetProperty("category");
        Assert.Contains("ApplicationError: running but failing requests", category.GetProperty("description").GetString(), StringComparison.Ordinal);

        // If this starts failing after a MAF / Microsoft.Extensions.AI upgrade, the per-value descriptions now reach
        // the model and the long description on Diagnosis.Category can be simplified.
        Assert.DoesNotContain("refuses connections", schema.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_answer_without_JSON_fails_with_a_clear_error()
    {
        // e.g. the model stops after tool calls without a final answer.
        var chat = new FakeChatClient().EnqueueText(string.Empty);
        var (agent, _) = CreateAgent(chat);

        var response = await OnCallAssistant.DiagnoseAsync(agent, OrdersErrors, Ct);

        // The run itself succeeds; reading Result is what fails (it deserializes on read).
        var ex = Assert.Throws<InvalidOperationException>(() => response.Result);
        Assert.Contains("did not contain JSON", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_answer_that_does_not_fit_Diagnosis_fails_loudly()
    {
        // Can't happen with a schema-enforcing model, but a model/provider without it would answer like this.
        var chat = new FakeChatClient().EnqueueText("""{ "rootCauseService": "catalog-api", "category": "Banana" }""");
        var (agent, _) = CreateAgent(chat);

        var response = await OnCallAssistant.DiagnoseAsync(agent, OrdersErrors, Ct);

        Assert.ThrowsAny<JsonException>(() => response.Result);
    }

    [Fact]
    public async Task The_response_carries_the_whole_run_usage_and_messages()
    {
        // What the eval runner reads besides the diagnosis: tokens of all round trips, and every tool call.
        var toolTurn = new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call-1", TelemetryTools.GetErrorLogsName, new Dictionary<string, object?> { ["service"] = "catalog-api" })]))
        { Usage = new UsageDetails { InputTokenCount = 1000, OutputTokenCount = 10 } };
        var answer = new ChatResponse(new ChatMessage(ChatRole.Assistant, DiagnosisJson))
        { Usage = new UsageDetails { InputTokenCount = 3000, OutputTokenCount = 200 } };
        var (agent, _) = CreateAgent(new FakeChatClient().Enqueue(toolTurn).Enqueue(answer));

        var response = await OnCallAssistant.DiagnoseAsync(agent, OrdersErrors, Ct);

        Assert.Equal(4000, response.Usage?.InputTokenCount); // summed over both round trips
        Assert.Equal(210, response.Usage?.OutputTokenCount);
        var call = Assert.Single(response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>());
        Assert.Equal(TelemetryTools.GetErrorLogsName, call.Name);
        Assert.Equal("catalog-api", response.Result.RootCauseService);
    }
}
