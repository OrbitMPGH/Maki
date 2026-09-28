namespace Maki.Api.Services;

/// <summary>Who asked for a recommendation pool, which decides whose slot budget it counts against.</summary>
public enum PoolOrigin
{
    /// <summary>The Recommended tab, Home's "You might like" and the Discover rails built on the recommender.</summary>
    Interactive,

    /// <summary>A custom rail. Each one is its own filter set, so a reader with several can fill a cache.</summary>
    Rail,
}

/// <summary>
/// <see cref="RecommendationService"/>'s pool cache: one dictionary with a separate slot budget per
/// <see cref="PoolOrigin"/>, least recently used first out within each. The split exists so a page of
/// custom rails can never evict the pool the Recommended tab is paging through. A key asked for by
/// both kinds of caller counts as interactive, which is also what lets "Show more" from a rail land
/// on the pool the rail already built.
/// <para>Not thread-safe: the service calls it under its own lock.</para>
/// </summary>
internal sealed class RecommendationPoolCache(int interactiveSlots, int railSlots, TimeSpan ttl, TimeProvider? clock = null)
{
    private sealed class Entry(RecommendationsResult pool, PoolOrigin origin, DateTime lastUsed)
    {
        public RecommendationsResult Pool { get; set; } = pool;
        public PoolOrigin Origin { get; set; } = origin;
        public DateTime LastUsed { get; set; } = lastUsed;
    }

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, Entry> _entries = [];

    public int Count => _entries.Count;

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public bool TryGet(string key, PoolOrigin origin, out RecommendationsResult pool)
    {
        pool = null!;
        if (!_entries.TryGetValue(key, out var entry))
        {
            return false;
        }

        if (Now - entry.Pool.GeneratedAt >= ttl)
        {
            _entries.Remove(key);
            return false;
        }

        entry.LastUsed = Now;
        if (origin == PoolOrigin.Interactive)
        {
            entry.Origin = PoolOrigin.Interactive;
        }

        pool = entry.Pool;
        return true;
    }

    public void Store(string key, RecommendationsResult pool, PoolOrigin origin)
    {
        if (_entries.TryGetValue(key, out var existing))
        {
            existing.Pool = pool;
            existing.LastUsed = Now;
            if (origin == PoolOrigin.Interactive)
            {
                existing.Origin = PoolOrigin.Interactive;
            }
        }
        else
        {
            _entries[key] = new Entry(pool, origin, Now);
        }

        foreach (var stale in _entries.Where(kv => Now - kv.Value.Pool.GeneratedAt >= ttl).Select(kv => kv.Key).ToList())
        {
            _entries.Remove(stale);
        }

        Trim(PoolOrigin.Interactive, interactiveSlots);
        Trim(PoolOrigin.Rail, railSlots);
    }

    private void Trim(PoolOrigin origin, int slots)
    {
        var held = _entries.Where(kv => kv.Value.Origin == origin).ToList();
        foreach (var (key, _) in held.OrderBy(kv => kv.Value.LastUsed).Take(Math.Max(0, held.Count - slots)))
        {
            _entries.Remove(key);
        }
    }
}
