namespace IncidentResponder.Tests.Fakes;

/// <summary>A clock stopped at a given instant, so time windows can be asserted exactly.</summary>
public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
