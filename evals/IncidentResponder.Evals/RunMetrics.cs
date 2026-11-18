using IncidentResponder.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace IncidentResponder.Evals;

/// <summary>
/// What a run cost and how the agent investigated, read from its <see cref="AgentResponse"/>.
/// Token counts are null when the provider didn't report them: unknown, never 0.
/// </summary>
/// <param name="InputTokens">All round trips together (Usage is already summed by the function-calling loop).</param>
/// <param name="CachedInputTokens">Part of <paramref name="InputTokens"/>, billed at a lower price.</param>
/// <param name="OutputTokens">All round trips together.</param>
/// <param name="ReasoningTokens">Part of <paramref name="OutputTokens"/>.</param>
/// <param name="RoundTrips">Model responses in the run: one assistant message each (tool requests or the answer).</param>
/// <param name="ToolCalls">Tool calls the model asked for, including refused and failed ones.</param>
/// <param name="ToolsUsed">Names of those tools, in order.</param>
/// <param name="BudgetHits">Tool results that are the middleware's budget-exhausted message.</param>
/// <param name="FailedToolCalls">Tool results that carry an exception.</param>
public sealed record RunMetrics(
    long? InputTokens,
    long? CachedInputTokens,
    long? OutputTokens,
    long? ReasoningTokens,
    int RoundTrips,
    int ToolCalls,
    IReadOnlyList<string> ToolsUsed,
    int BudgetHits,
    int FailedToolCalls)
{
    public static RunMetrics From(AgentResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var contents = response.Messages.SelectMany(m => m.Contents).ToList();
        var calls = contents.OfType<FunctionCallContent>().ToList();
        var results = contents.OfType<FunctionResultContent>().ToList();

        return new RunMetrics(
            response.Usage?.InputTokenCount,
            response.Usage?.CachedInputTokenCount,
            response.Usage?.OutputTokenCount,
            response.Usage?.ReasoningTokenCount, // null when not reported: unknown, not 0
            response.Messages.Count(m => m.Role == ChatRole.Assistant),
            calls.Count,
            [.. calls.Select(c => c.Name)],
            // The budget message is the tool's result, not message text (ChatMessage.Text only joins TextContent).
            results.Count(r => r.Result?.ToString()?.StartsWith(ToolCallMiddleware.BudgetExhaustedMessage, StringComparison.Ordinal) == true),
            results.Count(r => r.Exception is not null));
    }
}
