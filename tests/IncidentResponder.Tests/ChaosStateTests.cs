using IncidentLab.Chaos;

namespace IncidentResponder.Tests;

public sealed class ChaosStateTests
{
    [Fact]
    public void Activate_then_clear()
    {
        var state = new ChaosState(TimeProvider.System);

        var fault = state.Activate(new ChaosRequest(FaultKind.Latency, Probability: 0.5, DelayMs: 1500));

        Assert.Same(fault, state.Current);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), fault.Delay);

        state.Clear();
        Assert.Null(state.Current);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void Rejects_probability_out_of_range(double probability)
    {
        var state = new ChaosState(TimeProvider.System);

        Assert.Throws<ArgumentOutOfRangeException>(() => state.Activate(new ChaosRequest(FaultKind.Errors, probability)));
    }
}
