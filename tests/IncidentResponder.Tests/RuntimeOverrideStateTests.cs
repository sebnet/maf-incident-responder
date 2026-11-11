using IncidentLab.Platform;

namespace IncidentResponder.Tests;

public sealed class RuntimeOverrideStateTests
{
    [Fact]
    public void Activate_then_clear()
    {
        var state = new RuntimeOverrideState(TimeProvider.System);

        var active = state.Activate(new RuntimeOverrideRequest(OverrideKind.Latency, Probability: 0.5, DelayMs: 1500));

        Assert.Same(active, state.Current);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), active.Delay);

        state.Clear();
        Assert.Null(state.Current);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void Rejects_probability_out_of_range(double probability)
    {
        var state = new RuntimeOverrideState(TimeProvider.System);

        Assert.Throws<ArgumentOutOfRangeException>(() => state.Activate(new RuntimeOverrideRequest(OverrideKind.Errors, probability)));
    }

    [Fact]
    public void Hold_db_connections_keeps_connection_count_and_hold_time()
    {
        var state = new RuntimeOverrideState(TimeProvider.System);

        var active = state.Activate(new RuntimeOverrideRequest(OverrideKind.HoldDbConnections, DelayMs: 20_000, Connections: 20));

        Assert.Equal(20, active.Connections);
        Assert.Equal(TimeSpan.FromSeconds(20), active.Delay);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Rejects_connection_count_out_of_range(int connections)
    {
        var state = new RuntimeOverrideState(TimeProvider.System);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => state.Activate(new RuntimeOverrideRequest(OverrideKind.HoldDbConnections, Connections: connections)));
    }
}
