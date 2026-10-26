using System.Threading.RateLimiting;

namespace Maki.Core.Http;

/// <summary>
/// A token bucket that keeps no timer. <see cref="TokenBucketRateLimiter"/> with auto-replenishment
/// runs one per instance for the life of the process, and with a bucket per source that was about
/// 25 wakeups a second on an idle instance. This one works out from the clock when the next permit
/// is due (the GCRA form of a token bucket), and a request that has to wait sleeps until then.
/// <para>
/// Permits come back one at a time, every <c>period / tokensPerPeriod</c>, rather than in a batch
/// at the end of each period. The rate and the burst are the same. Waiters are served in the order
/// they asked, and the queue is unbounded, as the buckets it replaces were configured.
/// </para>
/// </summary>
public sealed class LazyTokenBucket : RateLimiter
{
    private static readonly Lease Acquired = new(true);

    private readonly TimeProvider _time;
    private readonly long _origin;
    private readonly long _interval;
    private readonly long _capacity;
    private readonly int _tokenLimit;
    private readonly CancellationTokenSource _disposed = new();
    private readonly Lock _lock = new();

    // When the bucket is next full, in ticks since _origin. A request takes the permits after it.
    private long _due;
    private long _queued;
    private long _successful;
    private long _failed;

    public LazyTokenBucket(int tokensPerPeriod, TimeSpan period, int tokenLimit, TimeProvider? time = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tokensPerPeriod, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(tokenLimit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(period, TimeSpan.Zero);
        _time = time ?? TimeProvider.System;
        _origin = _time.GetTimestamp();
        _interval = Math.Max(1, period.Ticks / tokensPerPeriod);
        _tokenLimit = tokenLimit;
        _capacity = _interval * tokenLimit;
    }

    private long Now => _time.GetElapsedTime(_origin).Ticks;

    public override TimeSpan? IdleDuration
    {
        get
        {
            lock (_lock)
            {
                var now = Now;
                return _queued == 0 && _due <= now ? TimeSpan.FromTicks(now - _due) : null;
            }
        }
    }

    public override RateLimiterStatistics? GetStatistics()
    {
        lock (_lock)
        {
            var now = Now;
            return new RateLimiterStatistics
            {
                CurrentAvailablePermits = Math.Max(0, (_capacity - (Math.Max(_due, now) - now)) / _interval),
                CurrentQueuedCount = _queued,
                TotalSuccessfulLeases = _successful,
                TotalFailedLeases = _failed,
            };
        }
    }

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(permitCount, _tokenLimit);
        lock (_lock)
        {
            var now = Now;
            var due = Math.Max(_due, now) + permitCount * _interval;
            var wait = due - now - _capacity;
            if (_disposed.IsCancellationRequested || wait > 0)
            {
                _failed++;
                return new Lease(false, TimeSpan.FromTicks(Math.Max(0, wait)));
            }

            _due = due;
            _successful++;
            return Acquired;
        }
    }

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(permitCount, _tokenLimit);
        long due;
        long wait;
        lock (_lock)
        {
            if (_disposed.IsCancellationRequested)
            {
                _failed++;
                return new Lease(false);
            }

            var now = Now;
            due = Math.Max(_due, now) + permitCount * _interval;
            wait = due - now - _capacity;
            _due = due;
            if (wait <= 0)
            {
                _successful++;
                return Acquired;
            }

            _queued++;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposed.Token);
        try
        {
            await Task.Delay(TimeSpan.FromTicks(wait), _time, linked.Token);
        }
        catch (OperationCanceledException)
        {
            lock (_lock)
            {
                _queued--;
                // Hand the permits back when nobody has queued behind them, so a cancelled request
                // does not push the next one back. Behind others they stay spent, which errs slow.
                if (_due == due)
                {
                    _due -= permitCount * _interval;
                }

                if (!ct.IsCancellationRequested)
                {
                    _failed++;
                    return new Lease(false);
                }
            }

            ct.ThrowIfCancellationRequested();
        }

        lock (_lock)
        {
            _queued--;
            _successful++;
        }

        return Acquired;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _disposed.Cancel();
        }

        base.Dispose(disposing);
    }

    private sealed class Lease(bool acquired, TimeSpan? retryAfter = null) : RateLimitLease
    {
        public override bool IsAcquired => acquired;

        public override IEnumerable<string> MetadataNames =>
            retryAfter is null ? [] : [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (retryAfter is { } after && metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = after;
                return true;
            }

            metadata = null;
            return false;
        }
    }
}
