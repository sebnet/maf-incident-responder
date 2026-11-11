using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace IncidentResponder.Tests.Fakes;

/// <summary>
/// Scripted IChatClient: returns queued responses in order and records what it was sent.
/// This is how agent behaviour is tested deterministically, with no model and no network.
/// Script the "model" (e.g. first a tool call, then a final answer) and assert on what the
/// agent did with it: which tools ran, with which arguments, what came back.
/// </summary>
public sealed class FakeChatClient : IChatClient
{
    private readonly Queue<ChatResponse> _responses = new();

    public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

    public FakeChatClient EnqueueText(string text) =>
        Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));

    public FakeChatClient EnqueueToolCall(string callId, string toolName, IDictionary<string, object?> arguments) =>
        Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, toolName, arguments)])));

    public FakeChatClient Enqueue(ChatResponse response)
    {
        _responses.Enqueue(response);
        return this;
    }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Requests.Add([.. messages]);
        return _responses.Count > 0
            ? Task.FromResult(_responses.Dequeue())
            : throw new InvalidOperationException("FakeChatClient: no scripted response left. The agent called the model more times than the test expected.");
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}
