using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Extensions.AI;

// The Responses API is marked experimental in the OpenAI SDK. Same suppression as the MAF sample this follows.
#pragma warning disable OPENAI001

namespace IncidentResponder.Agents;

/// <summary>
/// The ONLY place that knows which model provider is used. Everything else depends on IChatClient,
/// so tests can swap in a fake and a later phase can swap providers. See docs/adr/0003.
/// </summary>
public static class ChatClientFactory
{
    /// <summary>
    /// Foundry project → OpenAI Responses API → IChatClient.
    /// Same chain as agent-framework/dotnet/samples/02-agents/Harness/Harness_Step01_Research.
    /// Auth: DefaultAzureCredential (run `az login` first). Fine for a dev box; not for production.
    /// </summary>
    public static IChatClient CreateFoundry(Uri projectEndpoint, string deploymentName) =>
        new AIProjectClient(projectEndpoint, new DefaultAzureCredential())
            .GetProjectOpenAIClient()
            .GetResponsesClient()
            .AsIChatClient(deploymentName);
}
