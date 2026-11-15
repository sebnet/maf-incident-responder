namespace IncidentResponder.Agents;

/// <summary>Limits per agent run, enforced by <see cref="ToolCallMiddleware"/>. Bound to the "Budget" config section.</summary>
public sealed class BudgetOptions
{
    /// <summary>
    /// Maximum model round trips per run whose tool calls are executed. Each round trip re-sends the whole
    /// conversation, so this bounds the cost of a run. Parallel calls in one model response are one round trip.
    /// After the limit, tools are refused and the model is told to answer with what it has.
    /// Default when not configured: a safety net well above what a normal investigation needs (4 in s01 runs).
    /// </summary>
    public int MaxRoundTrips { get; set; } = 10;
}
