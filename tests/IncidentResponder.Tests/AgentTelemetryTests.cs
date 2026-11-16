using System.Collections.Concurrent;
using System.Diagnostics;
using IncidentResponder.Agents;
using IncidentResponder.Tests.Fakes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IncidentResponder.Tests;

/// <summary>
/// The agent emits OpenTelemetry spans on <see cref="OnCallAssistant.TelemetrySourceName"/> (GenAI semantic
/// conventions). Spans are captured in memory with an ActivityListener: no exporter, no collector.
/// </summary>
public sealed class AgentTelemetryTests
{
    private const string Question = "what's wrong with orders-api?";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task One_run_emits_an_agent_span_with_model_call_and_tool_call_spans()
    {
        var spans = await RunAndCaptureSpans(sensitiveTelemetry: false);

        Assert.Single(spans, s => s.DisplayName.StartsWith("invoke_agent", StringComparison.Ordinal));
        Assert.Equal(2, spans.Count(s => s.DisplayName.StartsWith("chat", StringComparison.Ordinal))); // tool call, then answer
        Assert.Single(spans, s => s.DisplayName.StartsWith("execute_tool", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Message_content_is_only_on_the_spans_when_sensitive_telemetry_is_on()
    {
        var off = await RunAndCaptureSpans(sensitiveTelemetry: false);
        var on = await RunAndCaptureSpans(sensitiveTelemetry: true);

        Assert.DoesNotContain(off, SpanMentionsTheQuestion);
        Assert.Contains(on, SpanMentionsTheQuestion);
    }

    private static bool SpanMentionsTheQuestion(Activity span) =>
        span.TagObjects.Any(t => t.Value?.ToString()?.Contains(Question, StringComparison.Ordinal) == true);

    /// <summary>
    /// Runs the agent once (one tool call, then an answer) under a parent span, and returns the spans of that trace only:
    /// other tests run in parallel on the same ActivitySource.
    /// </summary>
    private static async Task<List<Activity>> RunAndCaptureSpans(bool sensitiveTelemetry)
    {
        var stopped = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is OnCallAssistant.TelemetrySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var chat = new FakeChatClient()
            .EnqueueToolCall("call-1", TelemetryTools.GetErrorLogsName, new Dictionary<string, object?> { ["service"] = "orders-api" })
            .EnqueueText("Root cause: catalog-api.");
        var agent = OnCallAssistant.Create(
            chat,
            new TelemetryTools(new StubTelemetryQuery(), TimeProvider.System),
            new ToolCallMiddleware(NullLogger<ToolCallMiddleware>.Instance, Options.Create(new BudgetOptions())),
            sensitiveTelemetry);

        // Created directly (not through an ActivitySource) so it exists regardless of listeners.
        ActivityTraceId traceId;
        using (var parent = new Activity("test run").Start())
        {
            traceId = parent.TraceId;
            await agent.RunAsync(Question, cancellationToken: Ct);
        }

        return [.. stopped.Where(s => s.TraceId == traceId && s.Source.Name == OnCallAssistant.TelemetrySourceName)];
    }
}
