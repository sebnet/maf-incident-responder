using System.Diagnostics;
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
/// TODO(F1): measure the serialized size of each result and log a warning above a threshold.
/// </summary>
public sealed partial class ToolCallMiddleware(ILogger<ToolCallMiddleware> logger, IOptions<BudgetOptions> budget)
{
    private readonly int _maxRoundTrips = ValidateBudget(budget);

    public async ValueTask<object?> InvokeAsync(
        AIAgent agent,
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken)
    {
        var roundTrip = context.Iteration + 1; // 1-based, as a human counts
        if (roundTrip > _maxRoundTrips)
        {
            LogRefused(agent, context, roundTrip);
            return $"Round-trip budget exhausted ({_maxRoundTrips} round trips with tools). This tool was not run. " +
                   "Do not call more tools. Answer now with the evidence you already have.";
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await next(context, cancellationToken);
            LogSucceeded(agent, context, started);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(ex, agent, context, started);
            throw;
        }
    }

    // Guarded so the argument string is only built when the log line will actually be written.
    private void LogSucceeded(AIAgent agent, FunctionInvocationContext context, long started)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            // CA1873 false positive: the IsEnabled guard above is not recognised for [LoggerMessage] methods.
#pragma warning disable CA1873
            LogToolSucceeded(logger, agent.Name, context.Function.Name, FormatArgs(context), Stopwatch.GetElapsedTime(started).TotalMilliseconds);
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
            LogBudgetExhausted(logger, agent.Name, _maxRoundTrips, context.Function.Name, FormatArgs(context), roundTrip);
        }
    }

    private static int ValidateBudget(IOptions<BudgetOptions> budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        var max = budget.Value.MaxRoundTrips;
        return max >= 1
            ? max
            : throw new ArgumentOutOfRangeException(nameof(budget), max, "Budget:MaxRoundTrips must be at least 1.");
    }

    private static string FormatArgs(FunctionInvocationContext context) =>
        string.Join(", ", context.Arguments.Select(a => $"{a.Key}={a.Value}"));

    [LoggerMessage(Level = LogLevel.Information, Message = "[{Agent}] tool {Tool}({Args}) ok in {ElapsedMs:F0} ms")]
    private static partial void LogToolSucceeded(ILogger logger, string? agent, string tool, string args, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[{Agent}] tool {Tool}({Args}) failed in {ElapsedMs:F0} ms")]
    private static partial void LogToolFailed(ILogger logger, Exception exception, string? agent, string tool, string args, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[{Agent}] run reached its budget of {MaxRoundTrips} round trips: refused tool {Tool}({Args}) in round trip {RoundTrip}")]
    private static partial void LogBudgetExhausted(ILogger logger, string? agent, int maxRoundTrips, string tool, string args, int roundTrip);
}
