using Maki.Core;

namespace Maki.Core.Tests;

public class SharedBuildTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_cancelled_waiter_leaves_the_build_running_for_the_next_one()
    {
        var builds = new SharedBuild<string>();
        var started = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var runs = 0;
        string Build()
        {
            Interlocked.Increment(ref runs);
            started.Set();
            release.Wait(Timeout);
            return "built";
        }

        using var cts = new CancellationTokenSource();
        var first = builds.Join(Build).WaitAsync(cts.Token);
        Assert.True(started.Wait(Timeout));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        var second = builds.Join(Build);
        release.Set();

        Assert.Equal("built", await second.WaitAsync(Timeout));
        Assert.Equal(1, runs);
        Assert.False(builds.IsRunning);
    }

    [Fact]
    public async Task A_failed_build_reaches_its_waiters_and_the_next_join_starts_again()
    {
        var builds = new SharedBuild<string>();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => builds.Join(() => throw new InvalidOperationException()).WaitAsync(Timeout));

        Assert.False(builds.IsRunning);
        Assert.Equal("again", await builds.Join(() => "again").WaitAsync(Timeout));
    }

    [Fact]
    public async Task Drain_waits_for_the_running_build_and_swallows_its_failure()
    {
        var builds = new SharedBuild<string>();
        var release = new ManualResetEventSlim();
        _ = builds.Join(() =>
        {
            release.Wait(Timeout);
            throw new InvalidOperationException();
        });

        var drain = builds.DrainAsync();
        Assert.False(drain.IsCompleted);
        release.Set();
        await drain.WaitAsync(Timeout);
        Assert.False(builds.IsRunning);
    }
}
