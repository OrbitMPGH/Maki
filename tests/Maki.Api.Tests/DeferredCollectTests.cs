using Maki.Api.Services;

namespace Maki.Api.Tests;

public class DeferredCollectTests
{
    [Fact]
    public async Task Collects_once_after_the_worker_stays_idle()
    {
        var collected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        using var collect = new DeferredCollect(() =>
        {
            Interlocked.Increment(ref count);
            collected.TrySetResult();
        }, TimeSpan.FromMilliseconds(50));

        collect.Busy();
        collect.Worked();
        collect.Idle();
        await collected.Task.WaitAsync(TimeSpan.FromSeconds(10));

        collect.Busy();
        collect.Idle();
        await Task.Delay(300);

        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    public async Task New_work_before_the_quiet_period_ends_postpones_the_collect()
    {
        var count = 0;
        using var collect = new DeferredCollect(() => Interlocked.Increment(ref count), TimeSpan.FromMilliseconds(400));

        for (var i = 0; i < 5; i++)
        {
            collect.Busy();
            collect.Worked();
            collect.Idle();
            await Task.Delay(50);
        }

        collect.Busy();
        await Task.Delay(800);

        Assert.Equal(0, Volatile.Read(ref count));
    }

    [Fact]
    public async Task An_idle_worker_that_did_nothing_never_collects()
    {
        var count = 0;
        using var collect = new DeferredCollect(() => Interlocked.Increment(ref count), TimeSpan.FromMilliseconds(20));

        collect.Busy();
        collect.Idle();
        await Task.Delay(300);

        Assert.Equal(0, Volatile.Read(ref count));
    }
}
