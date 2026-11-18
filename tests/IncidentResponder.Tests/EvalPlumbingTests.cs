using IncidentResponder.Core;
using IncidentResponder.Evals;

namespace IncidentResponder.Tests;

/// <summary>The deterministic parts of the eval runner: scenarios, scoring, cost, summary. No model, no lab.</summary>
public sealed class EvalPlumbingTests
{
    private static string ScenariosDir => Path.Combine(RepoInfo.FindRoot(AppContext.BaseDirectory), "evals", "scenarios");

    private static readonly ScenarioExpectation S01Expected = new("catalog-api", RootCauseCategory.ApplicationError, ["catalog-api", "NaN"]);

    private static Diagnosis Diagnosis(
        string service = "catalog-api",
        RootCauseCategory category = RootCauseCategory.ApplicationError,
        string rootCause = "catalog-api fails reading prices: value 'NaN' is not a valid decimal",
        params Evidence[] evidence) =>
        new(service, category, rootCause, 0.9, evidence.Length > 0 ? evidence : [new Evidence("logs:catalog-api", "InvalidOperationException")], ["Repair the data"]);

    private static RunMetrics Metrics(long? input = 10_000, long? cached = 4_000, long? output = 500) =>
        new(input, cached, output, null, 3, 4, ["get_error_logs"], 0, 0);

    // ---------- Scenarios ----------

    [Fact]
    public void Loads_the_ready_scenarios_of_the_repo_with_their_ground_truth()
    {
        var scenarios = ScenarioLoader.LoadReady(ScenariosDir);

        var s01 = Assert.Single(scenarios, s => s.Id == "s01-catalog-errors");
        Assert.Equal("catalog-api", s01.Inject.Target);
        Assert.Equal("Errors", s01.Inject.Fault.GetProperty("kind").GetString());
        Assert.Equal("orders-api", s01.Alert.Service); // the agent is told the alerting service, not the target
        Assert.Equal(RootCauseCategory.ApplicationError, s01.Expected.Category);
        Assert.All(scenarios, s => Assert.True(s.IsReady));
    }

    [Fact]
    public void Filters_scenarios_by_id_prefix()
    {
        var scenarios = ScenarioLoader.LoadReady(ScenariosDir, ["s01", "s02"]);

        Assert.Equal(["s01-catalog-errors", "s02-orders-latency"], scenarios.Select(s => s.Id));
    }

    [Fact]
    public void A_filter_that_matches_nothing_is_an_error_not_a_silent_skip()
    {
        var ex = Assert.Throws<ArgumentException>(() => ScenarioLoader.LoadReady(ScenariosDir, ["s1"]));
        Assert.Contains("s01-catalog-errors", ex.Message, StringComparison.Ordinal); // lists what exists
    }

    // ---------- Scoring ----------

    [Fact]
    public void A_correct_diagnosis_scores_all_correct()
    {
        var score = Scorer.Score(S01Expected, Diagnosis(service: " Catalog-API "));

        Assert.True(score.AllCorrect);
    }

    [Fact]
    public void Wrong_service_and_wrong_category_are_scored_separately()
    {
        var score = Scorer.Score(S01Expected, Diagnosis(service: "orders-api", category: RootCauseCategory.DependencyFailure));

        Assert.False(score.ServiceCorrect);
        Assert.False(score.CategoryCorrect);
        Assert.True(score.MentionsOk);
        Assert.False(score.AllCorrect);
    }

    [Fact]
    public void Required_terms_count_in_the_root_cause_or_evidence_but_not_in_suggested_actions()
    {
        var diagnosis = new Diagnosis("catalog-api", RootCauseCategory.ApplicationError, "catalog-api throws on item reads", 0.9,
            [new Evidence("logs:catalog-api", "InvalidOperationException on every price read")],
            ["Check for NaN values"]); // "NaN" only here

        var score = Scorer.Score(S01Expected, diagnosis);

        Assert.False(score.MentionsOk);
        Assert.Equal(["NaN"], score.MissingMentions);
    }

    // ---------- Cost ----------

    [Fact]
    public void Cost_prices_cached_input_apart_from_fresh_input()
    {
        var price = new ModelPrice(InputPerMillion: 1.0m, CachedInputPerMillion: 0.25m, OutputPerMillion: 4.0m);

        // 6,000 fresh × 1 + 4,000 cached × 0.25 + 500 out × 4 = 6,000 + 1,000 + 2,000 = 9,000 per million.
        Assert.Equal(0.009m, CostEstimator.Estimate(price, Metrics()));
    }

    [Fact]
    public void Cost_is_unknown_without_a_price_or_without_token_counts()
    {
        var price = new ModelPrice(1.0m, 0.25m, 4.0m);

        Assert.Null(CostEstimator.Estimate(null, Metrics()));
        Assert.Null(CostEstimator.Estimate(price, Metrics(input: null)));
        Assert.Null(CostEstimator.Estimate(price, Metrics(output: null)));
    }

    [Fact]
    public void Cost_is_unknown_when_cached_exceeds_input_instead_of_priced_wrong()
    {
        // Would mean the provider doesn't count cached tokens inside input tokens: the formula would be wrong.
        Assert.Null(CostEstimator.Estimate(new ModelPrice(1.0m, 0.25m, 4.0m), Metrics(input: 1_000, cached: 2_000)));
    }

    [Fact]
    public void Unknown_cache_prices_all_input_as_fresh()
    {
        Assert.Equal(0.012m, CostEstimator.Estimate(new ModelPrice(1.0m, 0.25m, 4.0m), Metrics(cached: null)));
    }

    // ---------- Summary ----------

    [Fact]
    public void Summary_counts_errored_runs_as_wrong_and_does_not_hide_unknown_costs()
    {
        var at = DateTimeOffset.UnixEpoch;
        RunRecord Run(int rep, Score? score, decimal? cost, string? error) =>
            new(at, "phase-1", "s01-catalog-errors", rep, "gpt-x", "abc1234", "1.28.0", at, at, 10, null, score, error is null ? Metrics() : null, cost, error, null);

        var markdown = Summary.ToMarkdown(
        [
            Run(1, new Score(true, true, true, []), 0.01m, null),
            Run(2, new Score(true, false, true, []), null, null),
            Run(3, null, null, "JsonException: bad answer"),
        ]);

        var row = Assert.Single(markdown.Split('\n'), l => l.StartsWith("| s01", StringComparison.Ordinal));
        Assert.Contains("| 3 | 2/3 | 1/3 | 2/3 | 1/3 |", row, StringComparison.Ordinal);
        Assert.Contains("unknown (1/3 priced)", row, StringComparison.Ordinal);
        Assert.EndsWith("| 1 |", row.TrimEnd(), StringComparison.Ordinal); // errors
    }
}
