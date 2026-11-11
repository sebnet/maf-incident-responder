using System.Text.Json.Serialization;

namespace IncidentLab.Chaos;

/// <summary>Kinds of faults the lab can inject. Extend in Phase 0 (see docs/PLAN.md).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FaultKind>))]
public enum FaultKind
{
    /// <summary>Adds a delay before the request is handled.</summary>
    Latency,

    /// <summary>Throws an exception with a realistic message, producing a 500 and an error log.</summary>
    Errors,
}

/// <summary>A fault currently active in a service.</summary>
/// <param name="Kind">What kind of fault.</param>
/// <param name="Probability">Fraction of requests affected, 0..1.</param>
/// <param name="Delay">For <see cref="FaultKind.Latency"/>.</param>
/// <param name="ErrorMessage">
/// For <see cref="FaultKind.Errors"/>. Must read like a real production error
/// (e.g. a Npgsql timeout message). Never mention "chaos", "fault" or "injected".
/// </param>
public sealed record ActiveFault(FaultKind Kind, double Probability, TimeSpan? Delay, string? ErrorMessage, DateTimeOffset ActivatedAt);

/// <summary>Request body for <c>POST /chaos</c>.</summary>
public sealed record ChaosRequest(FaultKind Kind, double Probability = 1.0, int? DelayMs = null, string? ErrorMessage = null);

/// <summary>Holds the fault active in this service instance (one at a time). Thread-safe.</summary>
public sealed class ChaosState(TimeProvider timeProvider)
{
    private readonly Lock _gate = new();
    private ActiveFault? _current;

    public ActiveFault? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public ActiveFault Activate(ChaosRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Probability is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Probability must be between 0 and 1.");
        }

        var fault = new ActiveFault(
            request.Kind,
            request.Probability,
            request.DelayMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
            request.ErrorMessage,
            timeProvider.GetUtcNow());

        lock (_gate)
        {
            _current = fault;
        }

        return fault;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _current = null;
        }
    }
}
