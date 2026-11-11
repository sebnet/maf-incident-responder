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

    public static AIAgent Create(IChatClient chatClient, TelemetryTools tools, ToolCallMiddleware toolMiddleware)
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
            .Use(toolMiddleware.InvokeAsync)
            .Build();
    }
}
