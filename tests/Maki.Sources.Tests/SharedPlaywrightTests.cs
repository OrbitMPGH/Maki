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

    [Fact]
    public async Task The_heap_cap_is_set_only_while_the_driver_starts()
    {
        var before = Environment.GetEnvironmentVariable(DriverHeapCap.Variable);
        Environment.SetEnvironmentVariable(DriverHeapCap.Variable, null);
        try
        {
            using (DriverHeapCap.Apply())
            {
                Assert.Equal(DriverHeapCap.Value, Environment.GetEnvironmentVariable(DriverHeapCap.Variable));
            }

            Assert.Null(Environment.GetEnvironmentVariable(DriverHeapCap.Variable));

            using var shared = new SharedPlaywright();
            var driver = await shared.AcquireAsync();
            Assert.Null(Environment.GetEnvironmentVariable(DriverHeapCap.Variable));
            await shared.ReleaseAsync(driver);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DriverHeapCap.Variable, before);
        }
    }

    [Fact]
    public void An_admins_own_node_options_win()
    {
        var before = Environment.GetEnvironmentVariable(DriverHeapCap.Variable);
        Environment.SetEnvironmentVariable(DriverHeapCap.Variable, "--max-old-space-size=512");
        try
        {
            using (DriverHeapCap.Apply())
            {
                Assert.Equal("--max-old-space-size=512", Environment.GetEnvironmentVariable(DriverHeapCap.Variable));
            }

            Assert.Equal("--max-old-space-size=512", Environment.GetEnvironmentVariable(DriverHeapCap.Variable));
        }
        finally
        {
            Environment.SetEnvironmentVariable(DriverHeapCap.Variable, before);
        }
    }
}
