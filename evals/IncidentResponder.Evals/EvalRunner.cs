using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using IncidentResponder.Core;
using Microsoft.Agents.AI;

namespace IncidentResponder.Evals;

/// <param name="Model">Deployment name: recorded with every run and the key into <paramref name="Prices"/>.</param>
/// <param name="Settle">Clean time (no fault anywhere) before each injection.</param>
/// <param name="Baseline">How far before the injection the tools may look: the clean "before" the model compares with.</param>
public sealed record EvalSettings(
    string Phase,
    int Repetitions,
    TimeSpan Settle,
    TimeSpan Baseline,
    string Model,
    string Commit,
    string MafVersion,
    IReadOnlyDictionary<string, ModelPrice> Prices);

/// <summary>
/// The eval loop (evals/README.md). Per scenario and repetition:
/// clear all faults → settle → inject → warmup → diagnose with a fresh agent bounded to [injection − baseline, now]
/// → clear → score → append one JSON line.
/// </summary>
public sealed class EvalRunner(EvalContext context, LabControl lab, EvalSettings settings, TextWriter output)
{
    /// <summary>
    /// Source of the parent span around each diagnosis, so the agent's spans of one run share a trace whose id goes
    /// into the run's JSON line. Its attributes are neutral on purpose (phase and run number): no scenario id, no
    /// expected answer. Spans live in the same Tempo the agent's tools read, and must never give the answer away
    /// (AGENTS.md §6.5). The run → scenario link is the JSON line.
    /// </summary>
    public const string ActivitySourceName = "IncidentResponder.Evals";

    private static readonly ActivitySource Source = new(ActivitySourceName);
    // Relaxed escaping: the default escapes characters like + and ' (+, ') for HTML safety, which makes the
    // file hard to read and search ("c432642+dirty" would not be found). These files are never embedded in HTML.
    private static readonly JsonSerializerOptions JsonLine = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly List<RunRecord> _records = [];

    /// <summary>Runs finished so far (also when the loop was cancelled half-way).</summary>
    public IReadOnlyList<RunRecord> Records => _records;

    public async Task RunAsync(IReadOnlyList<Scenario> scenarios, string runsFile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        var total = scenarios.Count * settings.Repetitions;
        foreach (var scenario in scenarios)
        {
            for (var repetition = 1; repetition <= settings.Repetitions; repetition++)
            {
                output.WriteLine($"[{_records.Count + 1}/{total}] {scenario.Id} #{repetition}");
                var record = await RunOneAsync(scenario, repetition, cancellationToken).ConfigureAwait(false);
                _records.Add(record);
                await File.AppendAllTextAsync(runsFile, JsonSerializer.Serialize(record, JsonLine) + "\n", cancellationToken).ConfigureAwait(false);
                output.WriteLine("  " + Describe(record));
            }
        }
    }

    private async Task<RunRecord> RunOneAsync(Scenario scenario, int repetition, CancellationToken cancellationToken)
    {
        var time = context.Time;
        var startedAt = time.GetUtcNow();

        await lab.ClearAllAsync(cancellationToken).ConfigureAwait(false);
        output.WriteLine($"  settling {settings.Settle.TotalMinutes:0.#} min with no fault");
        await Task.Delay(settings.Settle, time, cancellationToken).ConfigureAwait(false);

        try
        {
            await lab.InjectAsync(scenario.Inject.Target, scenario.Inject.Fault, cancellationToken).ConfigureAwait(false);
            var injectedAt = time.GetUtcNow();
            var notBefore = injectedAt - settings.Baseline;
            output.WriteLine($"  fault injected into {scenario.Inject.Target}, warming up {scenario.WarmupSeconds} s");
            await Task.Delay(TimeSpan.FromSeconds(scenario.WarmupSeconds), time, cancellationToken).ConfigureAwait(false);

            var alert = new Alert(scenario.Alert.Service, scenario.Alert.Signal, scenario.Alert.Description, time.GetUtcNow());
            output.WriteLine($"  diagnosing: {alert.Description}");
            using var span = Source.StartActivity("eval run");
            span?.SetTag("eval.phase", settings.Phase).SetTag("eval.run", _records.Count + 1);
            var started = Stopwatch.GetTimestamp();
            RunMetrics? metrics = null;
            Diagnosis? diagnosis = null;
            Score? score = null;
            string? error = null;
            try
            {
                AgentResponse<Diagnosis> response = await DiagnosisRun.RunAsync(context, alert, notBefore, cancellationToken).ConfigureAwait(false);
                metrics = RunMetrics.From(response); // before Result: a run whose answer doesn't parse still has its numbers
                diagnosis = response.Result;
                score = Scorer.Score(scenario.Expected, diagnosis);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or NotImplementedException))
            {
                // A failed run is a result (scored as wrong), not a reason to stop the whole eval.
                error = $"{ex.GetType().Name}: {ex.Message}";
            }

            var latency = Stopwatch.GetElapsedTime(started).TotalSeconds;
            var cost = metrics is null ? null : CostEstimator.Estimate(settings.Prices.GetValueOrDefault(settings.Model), metrics);
            return new RunRecord(startedAt, settings.Phase, scenario.Id, repetition, settings.Model, settings.Commit, settings.MafVersion,
                injectedAt, notBefore, latency, diagnosis, score, metrics, cost, error, span?.TraceId.ToHexString());
        }
        finally
        {
            // Always, also on Ctrl+C or a crash: never leave the lab broken.
            await lab.ClearAllAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    public static string Describe(RunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Error is not null)
        {
            return $"ERROR {record.Error} ({record.LatencySeconds:0.0} s){TraceSuffix(record)}";
        }

        var s = record.Score!;
        var m = record.Metrics!;
        var cost = record.EstimatedCostUsd is { } c ? "$" + c.ToString("0.0000", CultureInfo.InvariantCulture) : "cost unknown";
        return $"service {Mark(s.ServiceCorrect)} ({record.Diagnosis!.RootCauseService}), " +
               $"category {Mark(s.CategoryCorrect)} ({record.Diagnosis.Category}), mentions {Mark(s.MentionsOk)} | " +
               $"{m.RoundTrips} round trips, {m.ToolCalls} tools, {m.InputTokens?.ToString(CultureInfo.InvariantCulture) ?? "?"} in / " +
               $"{m.OutputTokens?.ToString(CultureInfo.InvariantCulture) ?? "?"} out tokens, {cost} | {record.LatencySeconds:0.0} s" +
               TraceSuffix(record);
    }

    private static string Mark(bool ok) => ok ? "ok" : "WRONG";

    private static string TraceSuffix(RunRecord record) => record.TraceId is null ? string.Empty : $" | trace {record.TraceId}";
}
