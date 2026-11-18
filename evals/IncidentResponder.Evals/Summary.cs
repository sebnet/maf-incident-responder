using System.Globalization;
using System.Text;

namespace IncidentResponder.Evals;

/// <summary>Per-scenario summary as a markdown table, ready to paste into evals/results/phase-N.md.</summary>
public static class Summary
{
    public static string ToMarkdown(IReadOnlyList<RunRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var sb = new StringBuilder();
        sb.AppendLine("| Scenario | Runs | Service | Category | Mentions | All correct | Round trips (avg) | Tool calls (avg) | Tokens in / out (avg) | Est. cost (total) | Latency (avg) | Budget hits | Errors |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|");

        foreach (var group in records.GroupBy(r => r.ScenarioId))
        {
            var runs = group.ToList();
            var n = runs.Count;
            var metrics = runs.Where(r => r.Metrics is not null).Select(r => r.Metrics!).ToList();

            sb.Append(CultureInfo.InvariantCulture, $"| {group.Key} | {n} ")
              .Append(CultureInfo.InvariantCulture, $"| {Count(runs, s => s.ServiceCorrect)}/{n} ")
              .Append(CultureInfo.InvariantCulture, $"| {Count(runs, s => s.CategoryCorrect)}/{n} ")
              .Append(CultureInfo.InvariantCulture, $"| {Count(runs, s => s.MentionsOk)}/{n} ")
              .Append(CultureInfo.InvariantCulture, $"| {Count(runs, s => s.AllCorrect)}/{n} ")
              .Append(CultureInfo.InvariantCulture, $"| {Average(metrics.Select(m => (double?)m.RoundTrips))} ")
              .Append(CultureInfo.InvariantCulture, $"| {Average(metrics.Select(m => (double?)m.ToolCalls))} ")
              .Append(CultureInfo.InvariantCulture, $"| {Average(metrics.Select(m => (double?)m.InputTokens), "0")} / {Average(metrics.Select(m => (double?)m.OutputTokens), "0")} ")
              .Append(CultureInfo.InvariantCulture, $"| {TotalCost(runs)} ")
              .Append(CultureInfo.InvariantCulture, $"| {Average(runs.Select(r => (double?)r.LatencySeconds))} s ")
              .Append(CultureInfo.InvariantCulture, $"| {metrics.Sum(m => m.BudgetHits)} ")
              .AppendLine(CultureInfo.InvariantCulture, $"| {runs.Count(r => r.Error is not null)} |");
        }

        return sb.ToString();
    }

    /// <summary>Runs without a score (errors) count as wrong.</summary>
    private static int Count(List<RunRecord> runs, Func<Score, bool> predicate) =>
        runs.Count(r => r.Score is { } s && predicate(s));

    /// <summary>"?" when any value is unknown: an average over part of the runs would look complete.</summary>
    private static string Average(IEnumerable<double?> values, string format = "0.0")
    {
        var list = values.ToList();
        return list.Count == 0 || list.Any(v => v is null)
            ? "?"
            : list.Average(v => v!.Value).ToString(format, CultureInfo.InvariantCulture);
    }

    private static string TotalCost(List<RunRecord> runs) =>
        runs.Count > 0 && runs.All(r => r.EstimatedCostUsd is not null)
            ? "$" + runs.Sum(r => r.EstimatedCostUsd!.Value).ToString("0.0000", CultureInfo.InvariantCulture)
            : $"unknown ({runs.Count(r => r.EstimatedCostUsd is not null)}/{runs.Count} priced)";
}
