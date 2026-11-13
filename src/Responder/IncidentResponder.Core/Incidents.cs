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

/// <summary>
/// What is wrong in the root-cause service (not what its callers experience).
/// The [Description]s on the values are for humans only: they don't reach the model's schema (see Diagnosis.Category).
/// The string converter matters: the JSON schema sent to the model lists the names, so deserialization must read names
/// too, whatever JsonSerializerOptions are used.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<RootCauseCategory>))]
public enum RootCauseCategory
{
    [Description("The evidence is not enough to tell.")]
    Unknown,

    [Description("The service is running and reachable but fails requests because of its own code or data: exceptions, invalid data, bugs.")]
    ApplicationError,

    [Description("The service cannot reach something it depends on at all: it is down, refuses connections or times out at network level.")]
    DependencyFailure,

    [Description("Requests are slow but do not fail.")]
    Latency,

    [Description("The service cannot get or use database connections: pool exhausted, database down or refusing connections.")]
    DatabaseConnectivity,

    [Description("The problem started with a deployment or configuration change.")]
    BadDeployment,

    [Description("CPU, memory, threads or queues are exhausted.")]
    ResourceSaturation,
}

/// <summary>
/// The structured output of an investigation (Phase 1: produced with RunAsync&lt;Diagnosis&gt;).
/// Evals compare RootCauseService + Category against the scenario's ground truth.
/// Descriptions travel in the JSON schema the model receives, so they are prompt: keep them precise.
/// </summary>
public sealed record Diagnosis(
    [property: Description("Service where the root cause is, which may differ from the alerting service")] string RootCauseService,
    // Repeated here on purpose: the JSON schema lists only the enum names; each value's [Description] does NOT reach
    // the model (verified with MAF 1.28.0 / M.E.AI 10.11.0, see DiagnoseTests). This description is what the model reads.
    [property: Description(
        "What is wrong in the root-cause service. ApplicationError: running but failing requests because of its own code or data. " +
        "DependencyFailure: down, unreachable or timing out at network level. Latency: slow but not failing. " +
        "DatabaseConnectivity: cannot get or use database connections. BadDeployment: started with a deployment or config change. " +
        "ResourceSaturation: CPU, memory, threads or queues exhausted. Unknown: not enough evidence.")]
    RootCauseCategory Category,
    [property: Description("Root cause in one sentence")] string RootCause,
    [property: Description("0..1")] double Confidence,
    IReadOnlyList<Evidence> Evidence,
    IReadOnlyList<string> SuggestedActions);

/// <summary>A proposed remediation that needs human approval (Phase 3, RequestPort).</summary>
public sealed record RemediationProposal(string Action, string Target, string Risk, string Rationale);
