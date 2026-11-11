using System.Text.Json.Serialization;

namespace IncidentLab.Platform;

// This library is the lab's fault injection. Its type, namespace and file names are neutral on purpose: they show up
// in stack traces and request logs that the responder reads, and must not give the answer away (AGENTS.md §6.5).

/// <summary>Kinds of behaviour the lab can switch on. Extend in Phase 0 (see docs/PLAN.md).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OverrideKind>))]
public enum OverrideKind
{
    /// <summary>Adds a delay before the request is handled.</summary>
    Latency,

    /// <summary>Throws an exception with a realistic message, producing a 500 and an error log.</summary>
    Errors,
}

/// <summary>The override currently active in a service.</summary>
/// <param name="Kind">What kind of override.</param>
/// <param name="Probability">Fraction of requests affected, 0..1.</param>
/// <param name="Delay">For <see cref="OverrideKind.Latency"/>.</param>
/// <param name="ErrorMessage">
/// For <see cref="OverrideKind.Errors"/>. Must read like a real production error
/// (e.g. a Npgsql timeout message). Never mention "chaos", "fault" or "injected".
/// </param>
public sealed record RuntimeOverride(OverrideKind Kind, double Probability, TimeSpan? Delay, string? ErrorMessage, DateTimeOffset ActivatedAt);

/// <summary>Request body for <c>POST /_ops/runtime</c>.</summary>
public sealed record RuntimeOverrideRequest(OverrideKind Kind, double Probability = 1.0, int? DelayMs = null, string? ErrorMessage = null);

/// <summary>Holds the override active in this service instance (one at a time), in process memory. Thread-safe.</summary>
public sealed class RuntimeOverrideState(TimeProvider timeProvider)
{
    private readonly Lock _gate = new();
    private RuntimeOverride? _current;

    public RuntimeOverride? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public RuntimeOverride Activate(RuntimeOverrideRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Probability is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Probability must be between 0 and 1.");
        }

        var active = new RuntimeOverride(
            request.Kind,
            request.Probability,
            request.DelayMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
            request.ErrorMessage,
            timeProvider.GetUtcNow());

        lock (_gate)
        {
            _current = active;
        }

        return active;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _current = null;
        }
    }
}
