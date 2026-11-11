using System.ComponentModel;
using System.Text.Json.Serialization;

namespace IncidentResponder.Core;

/// <summary>An alert that opens an incident.</summary>
public sealed record Alert(string Service, string Signal, string Description, DateTimeOffset FiredAt);

/// <summary>A closed time range. Every telemetry query is bounded by one.</summary>
public sealed record TimeWindow(DateTimeOffset From, DateTimeOffset To)
{
    public static TimeWindow LastMinutes(TimeProvider time, int minutes)
    {
        ArgumentNullException.ThrowIfNull(time);
        var now = time.GetUtcNow();
        return new TimeWindow(now.AddMinutes(-minutes), now);
    }
}

/// <summary>A piece of evidence: where it came from and what it shows, in one or two sentences.</summary>
public sealed record Evidence(
    [property: Description("Where the evidence comes from, e.g. 'logs:orders-api' or 'trace:4bf92f...'")] string Source,
    [property: Description("What it shows, one or two sentences")] string Summary);

[JsonConverter(typeof(JsonStringEnumConverter<RootCauseCategory>))]
public enum RootCauseCategory
{
    Unknown,
    DependencyFailure,
    Latency,
    DatabaseConnectivity,
    BadDeployment,
    ResourceSaturation,
}

/// <summary>
/// The structured output of an investigation (Phase 1: produced with RunAsync&lt;Diagnosis&gt;).
/// Evals compare RootCauseService + Category against the scenario's ground truth.
/// </summary>
public sealed record Diagnosis(
    [property: Description("Service where the root cause is, which may differ from the alerting service")] string RootCauseService,
    RootCauseCategory Category,
    [property: Description("Root cause in one sentence")] string RootCause,
    [property: Description("0..1")] double Confidence,
    IReadOnlyList<Evidence> Evidence,
    IReadOnlyList<string> SuggestedActions);

/// <summary>A proposed remediation that needs human approval (Phase 3, RequestPort).</summary>
public sealed record RemediationProposal(string Action, string Target, string Risk, string Rationale);
