using IncidentResponder.Agents;
using IncidentResponder.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IncidentResponder.Evals;

/// <summary>What every run shares. The agent itself is NOT shared: each run builds its own (see DiagnosisRun).</summary>
/// <param name="SensitiveTelemetry">Put prompts, tool results and answers on the agent's spans (to inspect failed runs).</param>
public sealed record EvalContext(
    IChatClient ChatClient,
    ITelemetryQuery Telemetry,
    TimeProvider Time,
    IOptions<BudgetOptions> Budget,
    ILoggerFactory LoggerFactory,
    bool SensitiveTelemetry = false);

/// <summary>One diagnosis with the real model, for one scenario run.</summary>
public static class DiagnosisRun
{
    /// <summary>
    /// Builds a fresh agent for this run, whose tools never look before <paramref name="notBefore"/>, and diagnoses
    /// the alert. A fresh agent per run means no state is shared between runs (tools, middleware, session).
    /// Returns the whole response: the runner reads Result, Usage and Messages from it.
    /// </summary>
    public static Task<AgentResponse<Diagnosis>> RunAsync(
        EvalContext context, Alert alert, DateTimeOffset notBefore, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(alert);

        var agent = OnCallAssistant.Create(
            context.ChatClient,
            new TelemetryTools(context.Telemetry, context.Time, notBefore),
            new ToolCallMiddleware(context.LoggerFactory.CreateLogger<ToolCallMiddleware>(), context.Budget),
            context.SensitiveTelemetry);

        // DiagnoseAsync starts its own fresh session.
        return OnCallAssistant.DiagnoseAsync(agent, alert, cancellationToken);
    }
}
