using Maki.Core.Images;

namespace Maki.Core.Tests;

/// <summary>
/// The megapixel budget behind <see cref="ImageWorkGate"/>: what fits runs, what does not waits in
/// arrival order, and a cancelled waiter gives its place up.
/// </summary>
public class WeightedGateTests
{
    [Fact]
    public async Task Waiters_that_fit_the_budget_run_at_once()
    {
        var gate = new WeightedGate(10);

        await gate.WaitAsync(4, CancellationToken.None);
        await gate.WaitAsync(6, CancellationToken.None);
    }

    [Fact]
    public async Task A_waiter_over_the_remaining_budget_waits_for_a_release()
    {
        var gate = new WeightedGate(10);
        await gate.WaitAsync(8, CancellationToken.None);

        var second = gate.WaitAsync(4, CancellationToken.None);
        Assert.False(second.IsCompleted);

        gate.Release(8);
        await second.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_light_waiter_does_not_overtake_a_heavy_one_ahead_of_it()
    {
        var gate = new WeightedGate(10);
        await gate.WaitAsync(8, CancellationToken.None);

        var heavy = gate.WaitAsync(9, CancellationToken.None);
        var light = gate.WaitAsync(1, CancellationToken.None);
        Assert.False(light.IsCompleted);

        gate.Release(8);
        await heavy.WaitAsync(TimeSpan.FromSeconds(5));
        await light.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_cancelled_waiter_lets_the_ones_behind_it_through()
    {
        var gate = new WeightedGate(10);
        await gate.WaitAsync(5, CancellationToken.None);
        using var cancel = new CancellationTokenSource();

        var heavy = gate.WaitAsync(9, cancel.Token);
        var light = gate.WaitAsync(5, CancellationToken.None);
        Assert.False(light.IsCompleted);

        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => heavy);
        await light.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Weights_round_up_to_whole_megapixels_within_the_budget()
    {
        Assert.Equal(1, ImageWorkGate.Weigh(10, 10));
        Assert.Equal(4, ImageWorkGate.Weigh(1600, 2400));
        Assert.Equal(ImageWorkGate.BudgetMegapixels, ImageWorkGate.Weigh(20_000, 20_000));
    }
}
