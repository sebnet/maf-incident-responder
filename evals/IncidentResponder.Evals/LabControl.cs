using System.Net.Http.Json;
using System.Text.Json;

namespace IncidentResponder.Evals;

/// <summary>
/// The lab's fault control (<c>/_ops/runtime</c>). Only the eval runner may call it: the responder's tools never do,
/// that would be reading the answer key (AGENTS.md §6.5).
/// </summary>
public sealed class LabControl(HttpClient http, IReadOnlyDictionary<string, Uri> services)
{
    private const string RuntimePath = "_ops/runtime";

    public async Task ClearAllAsync(CancellationToken cancellationToken)
    {
        foreach (var (name, baseUri) in services)
        {
            using var response = await http.DeleteAsync(new Uri(baseUri, RuntimePath), cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, $"clear {name}", cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task InjectAsync(string target, JsonElement fault, CancellationToken cancellationToken)
    {
        if (!services.TryGetValue(target, out var baseUri))
        {
            throw new ArgumentException($"Unknown lab service '{target}'. Configured: {string.Join(", ", services.Keys)}.", nameof(target));
        }

        using var response = await http.PostAsJsonAsync(new Uri(baseUri, RuntimePath), fault, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, $"inject into {target}", cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string what, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException($"Lab control failed to {what}: {(int)response.StatusCode} {body}", null, response.StatusCode);
        }
    }
}
