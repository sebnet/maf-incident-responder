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

    /// <summary>
    /// A tool result above this many characters (compact JSON) logs a warning. Results are never cut: keeping them
    /// small is the tools' job ("summaries, not dumps"). Typical results are 0.5–2 KB; 4,000 chars is about 1,000 tokens,
    /// re-sent on every later round trip of the run.
    /// </summary>
    public int WarnResultChars { get; set; } = 4000;
}
