using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace IncidentResponder.Agents;

/// <summary>
/// Function-call middleware: runs around every tool invocation the agent makes.
/// Signature and registration follow agent-framework/dotnet/samples/02-agents/Agents/Agent_Step11_Middleware.
///
/// TODO(F1): add a per-run tool-call budget (e.g. max 15 calls) and fail gracefully when exceeded.
/// TODO(F1): measure the serialized size of each result and log a warning above a threshold.
/// </summary>
public sealed partial class ToolCallMiddleware(ILogger<ToolCallMiddleware> logger)
{
    public async ValueTask<object?> InvokeAsync(
        AIAgent agent,
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken)
    {
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

    private static string FormatArgs(FunctionInvocationContext context) =>
        string.Join(", ", context.Arguments.Select(a => $"{a.Key}={a.Value}"));

    [LoggerMessage(Level = LogLevel.Information, Message = "[{Agent}] tool {Tool}({Args}) ok in {ElapsedMs:F0} ms")]
    private static partial void LogToolSucceeded(ILogger logger, string? agent, string tool, string args, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[{Agent}] tool {Tool}({Args}) failed in {ElapsedMs:F0} ms")]
    private static partial void LogToolFailed(ILogger logger, Exception exception, string? agent, string tool, string args, double elapsedMs);
}
