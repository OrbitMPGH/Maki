using System.Threading.RateLimiting;
using Maki.Core.Http;
using Microsoft.Extensions.Time.Testing;

namespace Maki.Core.Tests;

public class LazyTokenBucketTests
{
    private sealed class CountingClock : FakeTimeProvider
    {
        public int Timers { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Timers++;
            return base.CreateTimer(callback, state, dueTime, period);
        }
    }

    private readonly CountingClock _time = new();

    private LazyTokenBucket Bucket(int perPeriod = 1, int limit = 2) =>
        new(perPeriod, TimeSpan.FromSeconds(1), limit, _time);

    [Fact]
    public async Task A_full_bucket_hands_out_its_burst_at_once()
    {
        using var bucket = Bucket(limit: 3);

        for (var i = 0; i < 3; i++)
        {
            using var lease = await bucket.AcquireAsync(1);
            Assert.True(lease.IsAcquired);
        }

        Assert.False(bucket.AttemptAcquire(1).IsAcquired);
    }

    [Fact]
    public async Task Past_the_burst_requests_wait_for_the_rate_in_order()
    {
        using var bucket = Bucket(perPeriod: 2, limit: 2);
        await bucket.AcquireAsync(1);
        await bucket.AcquireAsync(1);

        var third = bucket.AcquireAsync(1).AsTask();
        var fourth = bucket.AcquireAsync(1).AsTask();
        Assert.Equal(2, bucket.GetStatistics()!.CurrentQueuedCount);

        _time.Advance(TimeSpan.FromMilliseconds(499));
        Assert.False(third.IsCompleted);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True((await third).IsAcquired);
        Assert.False(fourth.IsCompleted);
        _time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.True((await fourth).IsAcquired);
        Assert.Equal(0, bucket.GetStatistics()!.CurrentQueuedCount);
    }

    [Fact]
    public async Task An_idle_bucket_refills_to_its_limit_and_no_further()
    {
        using var bucket = Bucket(limit: 2);
        await bucket.AcquireAsync(1);
        await bucket.AcquireAsync(1);
        Assert.Equal(0, bucket.GetStatistics()!.CurrentAvailablePermits);

        _time.Advance(TimeSpan.FromHours(1));

        Assert.Equal(2, bucket.GetStatistics()!.CurrentAvailablePermits);
        Assert.True(bucket.AttemptAcquire(1).IsAcquired);
        Assert.True(bucket.AttemptAcquire(1).IsAcquired);
        Assert.False(bucket.AttemptAcquire(1).IsAcquired);
    }

    [Fact]
    public async Task A_cancelled_wait_at_the_back_hands_its_permit_back()
    {
        using var bucket = Bucket(limit: 1);
        await bucket.AcquireAsync(1);

        using var cts = new CancellationTokenSource();
        var waiting = bucket.AcquireAsync(1, cts.Token).AsTask();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        var next = bucket.AcquireAsync(1).AsTask();
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.True((await next).IsAcquired);
    }

    [Fact]
    public async Task Disposing_fails_the_waiters_instead_of_leaving_them_hanging()
    {
        var bucket = Bucket(limit: 1);
        await bucket.AcquireAsync(1);
        var waiting = bucket.AcquireAsync(1).AsTask();

        bucket.Dispose();

        Assert.False((await waiting.WaitAsync(TimeSpan.FromSeconds(5))).IsAcquired);
    }

    [Fact]
    public void A_refused_attempt_says_when_to_retry()
    {
        using var bucket = Bucket(limit: 1);
        Assert.True(bucket.AttemptAcquire(1).IsAcquired);

        var refused = bucket.AttemptAcquire(1);

        Assert.False(refused.IsAcquired);
        Assert.True(refused.TryGetMetadata(MetadataName.RetryAfter, out var after));
        Assert.Equal(TimeSpan.FromSeconds(1), after);
    }

    [Fact]
    public async Task Only_a_request_that_has_to_wait_starts_a_timer()
    {
        using var bucket = Bucket(limit: 2);
        bucket.AttemptAcquire(1);
        await bucket.AcquireAsync(1);
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, _time.Timers);

        await bucket.AcquireAsync(1);
        await bucket.AcquireAsync(1);
        var waiting = bucket.AcquireAsync(1).AsTask();
        Assert.Equal(1, _time.Timers);

        _time.Advance(TimeSpan.FromSeconds(1));
        await waiting;
    }
}
