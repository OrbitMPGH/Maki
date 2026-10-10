using Maki.Sources.Common;

namespace Maki.Sources.Tests;

/// <summary>
/// Starts the real Node driver (no browser), since the reference counting is only worth anything if
/// the process it guards actually ends with the last holder.
/// </summary>
public class SharedPlaywrightTests
{
    [Fact]
    public async Task Both_browsers_get_one_driver_that_ends_with_the_last_of_them()
    {
        using var shared = new SharedPlaywright();

        var first = await shared.AcquireAsync();
        var second = await shared.AcquireAsync();
        Assert.Same(first, second);

        await shared.ReleaseAsync(first);
        var third = await shared.AcquireAsync();
        Assert.Same(first, third);

        await shared.ReleaseAsync(second);
        await shared.ReleaseAsync(third);
        var fresh = await shared.AcquireAsync();
        Assert.NotSame(first, fresh);
        await shared.ReleaseAsync(fresh);
    }

    [Fact]
    public async Task A_discarded_driver_is_replaced_and_its_other_holders_release_harmlessly()
    {
        using var shared = new SharedPlaywright();

        var dead = await shared.AcquireAsync();
        var otherHolder = await shared.AcquireAsync();
        await shared.DiscardAsync(dead);

        var fresh = await shared.AcquireAsync();
        Assert.NotSame(dead, fresh);

        await shared.ReleaseAsync(otherHolder);
        Assert.Same(fresh, await shared.AcquireAsync());
    }
}
