using System.Globalization;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Primitives;

namespace Maki.Data;

/// <summary>
/// The memory cache EF Core keeps its model and compiled queries in, with unused queries expiring.
///
/// <para>
/// EF Core's own cache never expires anything: every distinct LINQ shape stays compiled for the life
/// of the process, until the 10,240 size limit (about 1,000 entries, 10 each) forces compaction. A
/// NAS after four mostly idle hours held 482 compiled queries, mostly from startup, repair and
/// periodic jobs that would not run again for hours. Measured on Linux with the Docker image's
/// settings, letting about 600 entries from one visit expire left 6.4 MB less live managed heap and
/// 5 MB less anonymous memory than a build that kept them; JIT code did not shrink.
/// </para>
///
/// <para>
/// So a compiled query or command template unused for <see cref="DefaultWindowMinutes"/> expires.
/// The window is longer than the 15-minute jobs' interval, so the queries of every job that runs
/// at least that often stay compiled; a cold query costs a few milliseconds to compile again.
/// <c>MAKI_EF_QUERY_CACHE_MINUTES=0</c> keeps everything, as EF Core does by default.
/// </para>
///
/// <para>
/// The model (<see cref="ModelCacheKey"/>, runtime and design-time) is pinned: never expired and
/// never compacted, because rebuilding it is the expensive part. EF sets its own priority and size
/// on the entry after <see cref="CreateEntry"/> returns, so the pin is applied when the entry is
/// committed, not when it is created.
/// </para>
///
/// <para>
/// Expiring an entry only drops references. <c>ArtifactIdleUnloadJob</c> sweeps expired entries
/// every minute and runs the compacting collect once at least <see cref="MinEvictionsToCollect"/>
/// have gone, so a few stray expiries never pay for a blocking collection.
/// </para>
/// </summary>
public sealed class EfQueryCache : IMemoryCache
{
    public const string WindowMinutesVariable = "MAKI_EF_QUERY_CACHE_MINUTES";

    public const int DefaultWindowMinutes = 20;

    /// <summary>EF Core's own limit, kept: model 250 plus up to 999 query and command entries of 10.</summary>
    public const long SizeLimit = 10_240;

    /// <summary>About 64 queries with their command templates, roughly 1.4 MB of live heap (about 11 KB an entry, measured).</summary>
    public const int MinEvictionsToCollect = 128;

    public static EfQueryCache Shared { get; } = new(
        Window(ResolveMinutes(Environment.GetEnvironmentVariable(WindowMinutesVariable))));

    private readonly MemoryCache _inner;
    private long _evicted;

    public EfQueryCache(TimeSpan? window, ISystemClock? clock = null, long sizeLimit = SizeLimit)
    {
        SlidingWindow = window;
        var options = new MemoryCacheOptions { SizeLimit = sizeLimit };
        if (clock is not null)
        {
            options.Clock = clock;
        }

        _inner = new MemoryCache(options);
    }

    /// <summary>How long an unused query stays compiled; null keeps everything.</summary>
    public TimeSpan? SlidingWindow { get; }

    public int Count => _inner.Count;

    public IEnumerable<object> Keys => _inner.Keys;

    /// <summary>Entries expired or compacted away since the last <see cref="Collected"/>.</summary>
    public int PendingEvictions => (int)Math.Min(Volatile.Read(ref _evicted), int.MaxValue);

    /// <summary>
    /// Removes every expired entry now, then reports how many entries expired or were compacted
    /// away since the last <see cref="Collected"/>. The count can trail the removal slightly: the
    /// cache runs eviction callbacks on the thread pool, so an entry swept now may be counted on the
    /// next call.
    /// </summary>
    public int Sweep()
    {
        _inner.Compact(0);
        return PendingEvictions;
    }

    /// <summary>The memory of <paramref name="evictions"/> evicted entries has been collected.</summary>
    public void Collected(int evictions) => Interlocked.Add(ref _evicted, -evictions);

    public ICacheEntry CreateEntry(object key) => new Entry(this, _inner.CreateEntry(key), key is ModelCacheKey);

    public bool TryGetValue(object key, out object? value) => _inner.TryGetValue(key, out value);

    public void Remove(object key) => _inner.Remove(key);

    public void Dispose() => _inner.Dispose();

    /// <summary>Minutes before an unused query expires. Zero keeps everything; anything unusable falls back to the default.</summary>
    public static int ResolveMinutes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultWindowMinutes;
        }

        return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
            && minutes >= 0
                ? minutes
                : DefaultWindowMinutes;
    }

    private static TimeSpan? Window(int minutes) => minutes > 0 ? TimeSpan.FromMinutes(minutes) : null;

    private static void OnEvicted(object key, object? value, EvictionReason reason, object? state)
    {
        if (reason is EvictionReason.Expired or EvictionReason.Capacity or EvictionReason.TokenExpired)
        {
            Interlocked.Increment(ref ((EfQueryCache)state!)._evicted);
        }
    }

    private sealed class Entry(EfQueryCache owner, ICacheEntry inner, bool pinned) : ICacheEntry
    {
        public object Key => inner.Key;

        public object? Value
        {
            get => inner.Value;
            set => inner.Value = value;
        }

        public DateTimeOffset? AbsoluteExpiration
        {
            get => inner.AbsoluteExpiration;
            set => inner.AbsoluteExpiration = value;
        }

        public TimeSpan? AbsoluteExpirationRelativeToNow
        {
            get => inner.AbsoluteExpirationRelativeToNow;
            set => inner.AbsoluteExpirationRelativeToNow = value;
        }

        public TimeSpan? SlidingExpiration
        {
            get => inner.SlidingExpiration;
            set => inner.SlidingExpiration = value;
        }

        public IList<IChangeToken> ExpirationTokens => inner.ExpirationTokens;

        public IList<PostEvictionCallbackRegistration> PostEvictionCallbacks => inner.PostEvictionCallbacks;

        public CacheItemPriority Priority
        {
            get => inner.Priority;
            set => inner.Priority = value;
        }

        public long? Size
        {
            get => inner.Size;
            set => inner.Size = value;
        }

        public void Dispose()
        {
            if (pinned)
            {
                inner.Priority = CacheItemPriority.NeverRemove;
                inner.AbsoluteExpiration = null;
                inner.AbsoluteExpirationRelativeToNow = null;
                inner.SlidingExpiration = null;
            }
            else
            {
                if (owner.SlidingWindow is { } window
                    && inner.SlidingExpiration is null
                    && inner.AbsoluteExpiration is null
                    && inner.AbsoluteExpirationRelativeToNow is null)
                {
                    inner.SlidingExpiration = window;
                }

                inner.PostEvictionCallbacks.Add(new PostEvictionCallbackRegistration
                {
                    EvictionCallback = OnEvicted,
                    State = owner,
                });
            }

            inner.Dispose();
        }
    }
}
