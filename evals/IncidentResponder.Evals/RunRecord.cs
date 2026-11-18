using IncidentResponder.Core;

namespace IncidentResponder.Evals;

/// <summary>
/// One line of evals/runs/*.jsonl: everything needed to compare this run with a later one (what ran, with which
/// model, prompt version and MAF version, and what came out).
/// </summary>
/// <param name="Commit">Git commit of the repo (prompts are code), with "+dirty" if there were uncommitted changes.</param>
/// <param name="Error">Why the run didn't produce a usable diagnosis (it is then scored as wrong, not skipped).</param>
/// <param name="NotBefore">Lower bound of every tool's look-back window in this run.</param>
/// <param name="TraceId">Trace of the diagnosis in Tempo (service incident-responder-evals); null when not exported.</param>
public sealed record RunRecord(
    DateTimeOffset StartedAt,
    string Phase,
    string ScenarioId,
    int Repetition,
    string Model,
    string Commit,
    string MafVersion,
    DateTimeOffset InjectedAt,
    DateTimeOffset NotBefore,
    double LatencySeconds,
    Diagnosis? Diagnosis,
    Score? Score,
    RunMetrics? Metrics,
    decimal? EstimatedCostUsd,
    string? Error,
    string? TraceId);
