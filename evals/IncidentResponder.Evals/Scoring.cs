using IncidentResponder.Core;

namespace IncidentResponder.Evals;

/// <summary>Objective score of one diagnosis against the scenario's ground truth (evals/README.md, "Scoring").</summary>
public sealed record Score(bool ServiceCorrect, bool CategoryCorrect, bool MentionsOk, IReadOnlyList<string> MissingMentions)
{
    public bool AllCorrect => ServiceCorrect && CategoryCorrect && MentionsOk;
}

public static class Scorer
{
    public static Score Score(ScenarioExpectation expected, Diagnosis diagnosis)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(diagnosis);

        var serviceCorrect = string.Equals(diagnosis.RootCauseService.Trim(), expected.RootCauseService, StringComparison.OrdinalIgnoreCase);
        var categoryCorrect = diagnosis.Category == expected.Category;

        // Terms must appear in the root cause or the evidence: what the diagnosis claims and what backs it.
        // Suggested actions don't count, a term there isn't evidence of having found it.
        var text = string.Join('\n', [diagnosis.RootCause, .. diagnosis.Evidence.SelectMany(e => new[] { e.Source, e.Summary })]);
        var missing = expected.MustMention.Where(term => !text.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();

        return new Score(serviceCorrect, categoryCorrect, missing.Count == 0, missing);
    }
}
