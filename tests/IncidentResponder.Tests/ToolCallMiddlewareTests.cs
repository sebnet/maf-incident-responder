using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using IncidentResponder.Agents;
using IncidentResponder.Core;
using IncidentResponder.Tests.Fakes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IncidentResponder.Tests;

/// <summary>
/// The round-trip budget in ToolCallMiddleware. A "round trip" is one model response whose tool calls get executed;
/// the fake model scripts them so the limit can be checked exactly.
/// </summary>
public sealed class ToolCallMiddlewareTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (AIAgent Agent, StubTelemetryQuery Telemetry) CreateAgent(FakeChatClient chat, int maxRoundTrips) =>
        CreateAgent(chat, new BudgetOptions { MaxRoundTrips = maxRoundTrips }, NullLogger<ToolCallMiddleware>.Instance, new StubTelemetryQuery());

    private static (AIAgent Agent, StubTelemetryQuery Telemetry) CreateAgent(
        FakeChatClient chat, BudgetOptions budget, ILogger<ToolCallMiddleware> logger, StubTelemetryQuery telemetry)
    {
        var agent = OnCallAssistant.Create(
            chat,
            new TelemetryTools(telemetry, TimeProvider.System),
            new ToolCallMiddleware(logger, Options.Create(budget)));
        return (agent, telemetry);
    }

    /// <summary>One model response asking for get_error_logs, once per service (several = parallel calls).</summary>
    private static ChatResponse ToolTurn(params string[] services) =>
        new(new ChatMessage(ChatRole.Assistant,
            [.. services.Select(s => (AIContent)new FunctionCallContent($"call-{s}", TelemetryTools.GetErrorLogsName, new Dictionary<string, object?> { ["service"] = s }))]));

    private static FunctionResultContent ResultFor(IEnumerable<ChatMessage> request, string service) =>
        request.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single(r => r.CallId == $"call-{service}");

    [Fact]
    public async Task Runs_tools_within_the_budget_and_refuses_them_after_it()
    {
        var chat = new FakeChatClient()
            .Enqueue(ToolTurn("a"))
            .Enqueue(ToolTurn("b"))
            .Enqueue(ToolTurn("c")) // third round trip: over a budget of 2
            .EnqueueText("final answer");
        var (agent, telemetry) = CreateAgent(chat, maxRoundTrips: 2);

        var response = await agent.RunAsync("what's wrong?", cancellationToken: Ct);

        Assert.Equal(["a", "b"], telemetry.LogQueries.Select(q => q.Service)); // "c" never reached the tool
        var refusal = ResultFor(chat.Requests[3], "c");
        Assert.Contains("budget exhausted", refusal.Result?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("final answer", response.Text); // the model still answers after the refusal
    }

    [Fact]
    public async Task Parallel_calls_in_one_model_response_count_as_one_round_trip()
    {
        var chat = new FakeChatClient()
            .Enqueue(ToolTurn("a", "b", "c")) // one round trip, three tools
            .Enqueue(ToolTurn("d"))           // second round trip: over a budget of 1
            .EnqueueText("final answer");
        var (agent, telemetry) = CreateAgent(chat, maxRoundTrips: 1);

        await agent.RunAsync("what's wrong?", cancellationToken: Ct);

        Assert.Equal(["a", "b", "c"], telemetry.LogQueries.Select(q => q.Service).Order());
        Assert.Contains("budget exhausted", ResultFor(chat.Requests[2], "d").Result?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_budget_starts_again_on_every_run()
    {
        // Same agent (and middleware instance) for two questions, as in the CLI.
        var chat = new FakeChatClient()
            .Enqueue(ToolTurn("a")).EnqueueText("first answer")
            .Enqueue(ToolTurn("b")).EnqueueText("second answer");
        var (agent, telemetry) = CreateAgent(chat, maxRoundTrips: 1);
        var session = await agent.CreateSessionAsync(Ct);

        await agent.RunAsync("first question", session, cancellationToken: Ct);
        await agent.RunAsync("second question", session, cancellationToken: Ct);

        Assert.Equal(["a", "b"], telemetry.LogQueries.Select(q => q.Service));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Rejects_a_budget_below_one(int maxRoundTrips)
    {
        // 0 would mean "no tools at all": almost certainly a missing or mistyped setting, so fail at startup.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ToolCallMiddleware(NullLogger<ToolCallMiddleware>.Instance, Options.Create(new BudgetOptions { MaxRoundTrips = maxRoundTrips })));
    }

    [Fact]
    public void Unconfigured_budget_defaults_to_a_safety_net_of_10()
    {
        Assert.Equal(10, new BudgetOptions().MaxRoundTrips);
    }

    // ---------- Result size ----------

    [Fact]
    public async Task Logs_the_size_of_each_result_as_the_model_receives_it()
    {
        var logger = new RecordingLogger<ToolCallMiddleware>();
        var chat = new FakeChatClient().Enqueue(ToolTurn("a")).EnqueueText("final answer");
        var (agent, _) = CreateAgent(chat, new BudgetOptions(), logger, new StubTelemetryQuery());

        await agent.RunAsync("what's wrong?", cancellationToken: Ct);

        var line = Assert.Single(logger.Entries, e => e.Level == LogLevel.Information).Message;
        var match = Regex.Match(line, @"ok in \d+ ms, (\d+) chars \(~(\d+) tokens\)");
        Assert.True(match.Success, line);

        // The size is the compact JSON of what the model actually received.
        var sent = (JsonElement)ResultFor(chat.Requests[1], "a").Result!;
        Assert.Equal(JsonSerializer.Serialize(sent).Length, int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Warns_about_a_large_result_but_still_sends_it_whole()
    {
        var logger = new RecordingLogger<ToolCallMiddleware>();
        var telemetry = new StubTelemetryQuery();
        var longMessage = new string('x', 5000); // a tool that dumps instead of summarizing
        telemetry.Logs = new LogSummary("a", TimeWindow.LastMinutes(TimeProvider.System, 15), 1,
            [new LogGroup(longMessage, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null)]);
        var chat = new FakeChatClient().Enqueue(ToolTurn("a")).EnqueueText("final answer");
        var (agent, _) = CreateAgent(chat, new BudgetOptions { WarnResultChars = 4000 }, logger, telemetry);

        await agent.RunAsync("what's wrong?", cancellationToken: Ct);

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning).Message;
        Assert.Contains("above the warning threshold of 4000", warning, StringComparison.Ordinal);
        var sent = (JsonElement)ResultFor(chat.Requests[1], "a").Result!;
        Assert.Contains(longMessage, sent.GetRawText(), StringComparison.Ordinal); // never cut
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Rejects_a_result_size_threshold_below_one(int warnResultChars)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ToolCallMiddleware(NullLogger<ToolCallMiddleware>.Instance, Options.Create(new BudgetOptions { WarnResultChars = warnResultChars })));
    }
}
