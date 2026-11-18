using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IncidentResponder.Agents;

/// <summary>
/// Function-call middleware: runs around every tool invocation the agent makes.
/// Signature and registration follow agent-framework/dotnet/samples/02-agents/Agents/Agent_Step11_Middleware.
///
/// Round-trip budget: <see cref="FunctionInvocationContext.Iteration"/> is the function-calling loop's round-trip
/// counter for the current run (it starts again on every run, so no state is kept here). Observed with
/// Microsoft.Extensions.AI 10.11.0: it is 0-based for the tools of the model's first response, although its docs say
/// the first request is iteration 1. Parallel calls in one response share the same value, so they are allowed or
/// refused together. Over budget, the tool is not run and the model gets a message telling it to answer.
///
/// Result size: what the model receives is the tool result as JSON, and every later round trip of the run re-sends it.
/// For functions made with AIFunctionFactory, <c>next</c> already returns that JSON as a <see cref="JsonElement"/>
/// (indented). The size logged is its compact length in characters, with a rough token estimate (chars / 4). Whether
/// the indentation also travels to the model depends on the provider adapter: not verified (see it in the agent traces).
/// </summary>
public sealed partial class ToolCallMiddleware(ILogger<ToolCallMiddleware> logger, IOptions<BudgetOptions> budget)
{
    /// <summary>Start of the tool result the model gets when the budget is spent (the eval runner counts these).</summary>
    public const string BudgetExhaustedMessage = "Round-trip budget exhausted";

    private readonly BudgetOptions _budget = Validate(budget);

    public async ValueTask<object?> InvokeAsync(
        AIAgent agent,
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken)
    {
        var roundTrip = context.Iteration + 1; // 1-based, as a human counts
        if (roundTrip > _budget.MaxRoundTrips)
        {
            LogRefused(agent, context, roundTrip);
            return $"{BudgetExhaustedMessage} ({_budget.MaxRoundTrips} round trips with tools). This tool was not run. " +
                   "Do not call more tools. Answer now with the evidence you already have.";
        }

        var started = Stopwatch.GetTimestamp();
        object? result;
        try
        {
            result = await next(context, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(ex, agent, context, started);
            throw;
        }

        var chars = ResultChars(result);
        LogSucceeded(agent, context, started, chars);
        if (chars > _budget.WarnResultChars)
        {
            LogLarge(agent, context, chars);
        }

        return result; // never cut: the model always gets the full result
    }

    /// <summary>Compact JSON length of a tool result, i.e. the characters the model reads.</summary>
    private static int ResultChars(object? result) => result switch
    {
        null => 0,
        string s => s.Length,
        JsonElement json => CompactLength(json),
        _ => CompactLength(JsonSerializer.SerializeToElement(result, AIJsonUtilities.DefaultOptions)),
    };

    private static int CompactLength(JsonElement json)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) // not indented by default
        {
            json.WriteTo(writer);
        }

        return Encoding.UTF8.GetCharCount(buffer.WrittenSpan);
    }

    private static int EstimateTokens(int chars) => (chars + 3) / 4;

    // Guarded so the argument string is only built when the log line will actually be written.
    private void LogSucceeded(AIAgent agent, FunctionInvocationContext context, long started, int chars)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            // CA1873 false positive: the IsEnabled guard above is not recognised for [LoggerMessage] methods.
#pragma warning disable CA1873
            LogToolSucceeded(logger, agent.Name, context.Function.Name, FormatArgs(context), Stopwatch.GetElapsedTime(started).TotalMilliseconds, chars, EstimateTokens(chars));
#pragma warning restore CA1873
        }
    }

    private void LogFailed(Exception ex, AIAgent agent, FunctionInvocationContext context, long started)
    {
        if (logger.IsEnabled(LogLevel.Warning))
        {
            LogToolFailed(logger, ex, agent.Name, context.Function.Name, FormatArgs(context), Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private void LogRefused(AIAgent agent, FunctionInvocationContext context, int roundTrip)
    {
        if (logger.IsEnabled(LogLevel.Warning))
        {
            LogBudgetExhausted(logger, agent.Name, _budget.MaxRoundTrips, context.Function.Name, FormatArgs(context), roundTrip);
        }
    }

    private void LogLarge(AIAgent agent, FunctionInvocationContext context, int chars)
    {
        if (logger.IsEnabled(LogLevel.Warning))
        {
            LogLargeResult(logger, agent.Name, context.Function.Name, FormatArgs(context), chars, EstimateTokens(chars), _budget.WarnResultChars);
        }
    }

    private static BudgetOptions Validate(IOptions<BudgetOptions> budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        var value = budget.Value;
        if (value.MaxRoundTrips < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(budget), value.MaxRoundTrips, "Budget:MaxRoundTrips must be at least 1.");
        }

        if (value.WarnResultChars < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(budget), value.WarnResultChars, "Budget:WarnResultChars must be at least 1.");
        }

        return value;
    }

    private static string FormatArgs(FunctionInvocationContext context) =>
        string.Join(", ", context.Arguments.Select(a => $"{a.Key}={a.Value}"));

    [LoggerMessage(Level = LogLevel.Information, Message = "[{Agent}] tool {Tool}({Args}) ok in {ElapsedMs:F0} ms, {ResultChars} chars (~{ResultTokens} tokens)")]
    private static partial void LogToolSucceeded(ILogger logger, string? agent, string tool, string args, double elapsedMs, int resultChars, int resultTokens);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[{Agent}] tool {Tool}({Args}) failed in {ElapsedMs:F0} ms")]
    private static partial void LogToolFailed(ILogger logger, Exception exception, string? agent, string tool, string args, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[{Agent}] run reached its budget of {MaxRoundTrips} round trips: refused tool {Tool}({Args}) in round trip {RoundTrip}")]
    private static partial void LogBudgetExhausted(ILogger logger, string? agent, int maxRoundTrips, string tool, string args, int roundTrip);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[{Agent}] tool {Tool}({Args}) returned {ResultChars} chars (~{ResultTokens} tokens), above the warning threshold of {WarnResultChars}: tools should return summaries")]
    private static partial void LogLargeResult(ILogger logger, string? agent, string tool, string args, int resultChars, int resultTokens, int warnResultChars);
}
