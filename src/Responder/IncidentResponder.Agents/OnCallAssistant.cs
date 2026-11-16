using IncidentResponder.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace IncidentResponder.Agents;

/// <summary>
/// Phase 1: a single ChatClientAgent with read-only telemetry tools.
/// In Phase 3 this same agent becomes an executor inside the investigation workflow.
/// </summary>
public static class OnCallAssistant
{
    public const string Name = "OnCallAssistant";

    // Prompt is versioned in git on purpose: every change is visible in eval results.
    public const string Instructions =
        """
        You are an on-call SRE assistant for a small e-commerce system with these services:
        - orders-api: receives orders, calls catalog-api for prices.
        - catalog-api: product catalog.
        - loadgen: synthetic traffic, ignore it as a root cause.

        How to investigate:
        1. Start from symptoms: error logs and metrics of the service the user mentions.
        2. Follow dependencies: if errors mention a downstream call, investigate that service too.
           The root cause is often NOT in the service that alerts.
        3. Use a sample trace id to confirm where in the call chain the failure happens.
        4. Never guess. If evidence is insufficient, say what is missing.

        When you answer, give: the root cause service, the category, the root cause in one sentence,
        your confidence (0-1), the evidence (source + one-line summary each) and suggested actions.
        You can only read telemetry. You cannot change anything in the system.
        """;

    /// <summary>
    /// The user message for a diagnosis. Part of the prompt: changes here are prompt changes (AGENTS.md §6.8).
    /// </summary>
    public static string DiagnoseRequest(Alert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        return $"""
            Alert fired at {alert.FiredAt.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC on {alert.Service} (signal: {alert.Signal}):
            {alert.Description}
            Investigate it with the tools and return your diagnosis.
            """;
    }

    /// <summary>
    /// Investigates an alert with structured output. The response is the full record of the run:
    /// <c>Result</c> is the typed <see cref="Diagnosis"/>, <c>Usage</c> the tokens of all round trips together, and
    /// <c>Messages</c> every tool call, tool result and the final answer (what the eval runner counts).
    /// <c>Result</c> deserializes when read, so an answer that doesn't fit <see cref="Diagnosis"/> throws there.
    /// Used by the CLI's /diagnose command and by the eval runner.
    /// Always starts a fresh session: a diagnosis must not depend on earlier chat, so runs are comparable.
    /// </summary>
    public static async Task<AgentResponse<Diagnosis>> DiagnoseAsync(AIAgent agent, Alert alert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(alert);

        AgentSession agentSession = await agent.CreateSessionAsync(cancellationToken);
        return await agent.RunAsync<Diagnosis>(DiagnoseRequest(alert), agentSession, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// ActivitySource and Meter name of the agent's OpenTelemetry spans and metrics. The host (CLI, eval runner)
    /// subscribes to it and chooses the exporter; this project only emits.
    /// </summary>
    public const string TelemetrySourceName = "IncidentResponder.Agents";

    /// <param name="sensitiveTelemetry">
    /// Also put prompts, tool arguments, tool results and answers on the spans. Only for the local lab.
    /// </param>
    public static AIAgent Create(IChatClient chatClient, TelemetryTools tools, ToolCallMiddleware toolMiddleware, bool sensitiveTelemetry = false)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(toolMiddleware);

        return chatClient
            .AsAIAgent(
                instructions: Instructions,
                name: Name,
                description: "Investigates production symptoms using read-only logs, metrics and traces.",
                tools: tools.AsAITools())
            .AsBuilder()
            // Agent-level telemetry: an invoke_agent span per run, and MAF also turns on its chat telemetry below the
            // function-calling loop, so each model round trip gets its own chat span and each tool an execute_tool span.
            // Outermost (first Use), so it sees every run, including runs other middleware might stop.
            .UseOpenTelemetry(TelemetrySourceName, configure: agent => agent.EnableSensitiveData = sensitiveTelemetry)
            .Use(toolMiddleware.InvokeAsync)
            .Build();
    }
}
