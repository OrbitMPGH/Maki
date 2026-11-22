using Maki.Api.Services;

namespace Maki.Api.Tests;

/// <summary>
/// <see cref="RecommendationPoolCache"/>: the split slot budget that keeps a page of custom rails
/// from evicting the pool the Recommended tab is paging through.
/// </summary>
public class RecommendationPoolCacheTests
{
    private static CachedRecommendationPool Pool(DateTime generatedAt) =>
        new([], CompactSimilar: null, Seeds: null, HydratedSimilar: [], generatedAt, BuildId: 1, DumpStamp: string.Empty);

    [Fact]
    public void Rail_inserts_never_evict_interactive_entries()
    {
        var clock = new StoppedClock(DateTimeOffset.UtcNow);
        var cache = new RecommendationPoolCache(interactiveSlots: 2, railSlots: 2, ttl: TimeSpan.FromHours(12), clock);

        cache.Store("interactive-1", Pool(clock.Now.UtcDateTime), PoolOrigin.Interactive);
        cache.Store("interactive-2", Pool(clock.Now.UtcDateTime), PoolOrigin.Interactive);

        for (var i = 0; i < 5; i++)
        {
            cache.Store($"rail-{i}", Pool(clock.Now.UtcDateTime), PoolOrigin.Rail);
        }

        Assert.True(cache.TryGet("interactive-1", PoolOrigin.Interactive, out _));
        Assert.True(cache.TryGet("interactive-2", PoolOrigin.Interactive, out _));
    }

    [Fact]
    public void Storing_beyond_a_budget_evicts_the_least_recently_used_entry_in_that_budget()
    {
        var clock = new StoppedClock(DateTimeOffset.UtcNow);
        var cache = new RecommendationPoolCache(interactiveSlots: 1, railSlots: 1, ttl: TimeSpan.FromHours(12), clock);

        cache.Store("rail-1", Pool(clock.Now.UtcDateTime), PoolOrigin.Rail);
        clock.Now = clock.Now.AddMinutes(1);
        cache.Store("rail-2", Pool(clock.Now.UtcDateTime), PoolOrigin.Rail);

        Assert.False(cache.TryGet("rail-1", PoolOrigin.Rail, out _));
        Assert.True(cache.TryGet("rail-2", PoolOrigin.Rail, out _));
    }

    [Fact]
    public void A_hit_touches_last_used_so_it_outlives_an_entry_that_was_never_reread()
    {
        var clock = new StoppedClock(DateTimeOffset.UtcNow);
        var cache = new RecommendationPoolCache(interactiveSlots: 1, railSlots: 2, ttl: TimeSpan.FromHours(12), clock);

        cache.Store("rail-1", Pool(clock.Now.UtcDateTime), PoolOrigin.Rail);
        clock.Now = clock.Now.AddMinutes(1);
        cache.Store("rail-2", Pool(clock.Now.UtcDateTime), PoolOrigin.Rail);

        // Touch rail-1 so it becomes the more recently used of the two.
        clock.Now = clock.Now.AddMinutes(1);
        Assert.True(cache.TryGet("rail-1", PoolOrigin.Rail, out _));

        clock.Now = clock.Now.AddMinutes(1);
        cache.Store("rail-3", Pool(clock.Now.UtcDateTime), PoolOrigin.Rail);

        Assert.True(cache.TryGet("rail-1", PoolOrigin.Rail, out _));
        Assert.False(cache.TryGet("rail-2", PoolOrigin.Rail, out _));
    }

    [Fact]
    public void An_interactive_hit_promotes_a_rail_entry_so_it_survives_rail_pressure()
    {
        var clock = new StoppedClock(DateTimeOffset.UtcNow);
        var cache = new RecommendationPoolCache(interactiveSlots: 5, railSlots: 1, ttl: TimeSpan.FromHours(12), clock);

        cache.Store("shared-key", Pool(clock.Now.UtcDateTime), PoolOrigin.Rail);
        // "Show more" from the rail's own tab lands on the same key as an interactive caller.
        Assert.True(cache.TryGet("shared-key", PoolOrigin.Interactive, out _));

        // Filling the (one-slot) rail budget twice over must not evict it now it counts as interactive.
        clock.Now = clock.Now.AddMinutes(1);
        cache.Store("rail-2", Pool(clock.Now.UtcDateTime), PoolOrigin.Rail);
        clock.Now = clock.Now.AddMinutes(1);
        cache.Store("rail-3", Pool(clock.Now.UtcDateTime), PoolOrigin.Rail);

        Assert.True(cache.TryGet("shared-key", PoolOrigin.Rail, out _));
    }

    [Fact]
    public void The_idle_sweep_keeps_interactive_pools_past_the_rail_window()
    {
        var clock = new StoppedClock(DateTimeOffset.UtcNow);
        var cache = new RecommendationPoolCache(interactiveSlots: 2, railSlots: 2, ttl: TimeSpan.FromHours(12), clock);
        cache.Store("interactive", Pool(clock.Now.UtcDateTime), PoolOrigin.Interactive);
        cache.Store("rail", Pool(clock.Now.UtcDateTime), PoolOrigin.Rail);

        clock.Now = clock.Now.AddMinutes(30);
        Assert.Equal(1, cache.ReleaseIdle(TimeSpan.FromMinutes(15), TimeSpan.FromHours(2)));
        Assert.True(cache.TryGet("interactive", PoolOrigin.Interactive, out _));
        Assert.False(cache.TryGet("rail", PoolOrigin.Rail, out _));

        clock.Now = clock.Now.AddHours(2);
        Assert.Equal(1, cache.ReleaseIdle(TimeSpan.FromMinutes(15), TimeSpan.FromHours(2)));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void An_entry_past_ttl_is_treated_as_a_miss()
    {
        var clock = new StoppedClock(DateTimeOffset.UtcNow);
        var cache = new RecommendationPoolCache(interactiveSlots: 5, railSlots: 5, ttl: TimeSpan.FromHours(12), clock);

        cache.Store("key", Pool(clock.Now.UtcDateTime), PoolOrigin.Interactive);
        Assert.True(cache.TryGet("key", PoolOrigin.Interactive, out _));

        clock.Now = clock.Now.AddHours(12);
        Assert.False(cache.TryGet("key", PoolOrigin.Interactive, out _));
    }
}
