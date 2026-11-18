using System.Text.Json;
using IncidentResponder.Core;

namespace IncidentResponder.Evals;

/// <summary>One file in evals/scenarios. Format: evals/README.md. Ground truth is written by a human, never a model.</summary>
public sealed record Scenario(
    string Id,
    string Title,
    string Status,
    ScenarioInjection Inject,
    int WarmupSeconds,
    ScenarioAlert Alert,
    ScenarioExpectation Expected)
{
    public bool IsReady => Status == "ready";
}

/// <param name="Target">Service whose <c>/_ops/runtime</c> receives the fault.</param>
/// <param name="Fault">Body for <c>POST /_ops/runtime</c>, sent as-is.</param>
public sealed record ScenarioInjection(string Target, JsonElement Fault);

/// <summary>What the agent is told. Never the target or the cause.</summary>
public sealed record ScenarioAlert(string Service, string Signal, string Description);

public sealed record ScenarioExpectation(string RootCauseService, RootCauseCategory Category, IReadOnlyList<string> MustMention);

public static class ScenarioLoader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Ready scenarios, in file name order. <paramref name="only"/> filters by id prefix ("s01" matches
    /// "s01-catalog-errors"); an entry that matches nothing is an error, so a typo doesn't silently skip a scenario.
    /// </summary>
    public static IReadOnlyList<Scenario> LoadReady(string directory, IReadOnlyCollection<string>? only = null)
    {
        var all = Directory.GetFiles(directory, "*.json")
            .Order(StringComparer.Ordinal)
            .Select(file => JsonSerializer.Deserialize<Scenario>(File.ReadAllText(file), Json)
                            ?? throw new InvalidDataException($"Empty scenario file: {file}"))
            .ToList();

        var ready = all.Where(s => s.IsReady).ToList();
        if (only is null || only.Count == 0)
        {
            return ready;
        }

        foreach (var prefix in only)
        {
            if (!ready.Any(s => s.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException(
                    $"No ready scenario matches '{prefix}'. Ready: {string.Join(", ", ready.Select(s => s.Id))}.", nameof(only));
            }
        }

        return [.. ready.Where(s => only.Any(p => s.Id.StartsWith(p, StringComparison.OrdinalIgnoreCase)))];
    }
}
