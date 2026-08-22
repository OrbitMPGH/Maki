using Maki.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Core.Tests;

/// <summary>
/// The cache that stops a bulk enqueue listing one series' catalog once per queued chapter. That
/// fan-out is what buried the shared rate limiter, timed the whole batch out against the HttpClient
/// timeout, and left the queue cycling between Failed and the five-minute retry sweep.
/// </summary>
public class SourceChapterListCacheTests
{
    private sealed class CountingSource : ISource
    {
        public string Name => "counting";
        public string DisplayName => Name;
        public string BaseUrl => "https://counting.test";
        public SourceCapabilities Capabilities => SourceCapabilities.None;

        public int ListCalls;
        public TimeSpan Delay = TimeSpan.Zero;
        public Exception? Throws;

        public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
            string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref ListCalls);
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, ct);
            }

            if (Throws is not null)
            {
                throw Throws;
            }

            return [new(Name, sourceSeriesId, "1", "1", 1m, null, null, languageFilter ?? "en", null)];
        }

        public Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class StoppedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static SourceChapterListCache NewCache(TimeProvider? time = null) =>
        new(time ?? TimeProvider.System, NullLogger<SourceChapterListCache>.Instance);

    [Fact]
    public async Task Repeated_lookups_for_one_series_list_it_once()
    {
        var source = new CountingSource();
        var cache = NewCache();

        for (var i = 0; i < 50; i++)
        {
            await cache.GetAsync(source, "series-1", "en");
        }

        Assert.Equal(1, source.ListCalls);
    }

    /// <summary>
    /// The case that actually happens: every chapter of a series is enqueued at once and each fires
    /// its own detached resolve, so the calls overlap rather than arriving one after another. Without
    /// single-flighting, the TTL alone would let all of them miss and go to the network together.
    /// </summary>
    [Fact]
    public async Task Concurrent_lookups_for_one_series_share_a_single_listing()
    {
        var source = new CountingSource { Delay = TimeSpan.FromMilliseconds(50) };
        var cache = NewCache();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 25).Select(_ => cache.GetAsync(source, "series-1", "en")));

        Assert.Equal(1, source.ListCalls);
        Assert.All(results, r => Assert.Single(r));
    }

    [Fact]
    public async Task Different_series_and_language_are_cached_separately()
    {
        var source = new CountingSource();
        var cache = NewCache();

        await cache.GetAsync(source, "series-1", "en");
        await cache.GetAsync(source, "series-2", "en");
        await cache.GetAsync(source, "series-1", "es");
        await cache.GetAsync(source, "series-1", null);

        Assert.Equal(4, source.ListCalls);
    }

    [Fact]
    public async Task A_listing_is_re_fetched_once_the_ttl_has_passed()
    {
        var clock = new StoppedClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var source = new CountingSource();
        var cache = NewCache(clock);

        await cache.GetAsync(source, "series-1", "en");
        clock.Now = clock.Now.Add(SourceChapterListCache.Ttl - TimeSpan.FromSeconds(1));
        await cache.GetAsync(source, "series-1", "en");
        Assert.Equal(1, source.ListCalls);

        clock.Now = clock.Now.AddSeconds(2);
        await cache.GetAsync(source, "series-1", "en");
        Assert.Equal(2, source.ListCalls);
    }

    /// <summary>
    /// A source that was briefly down must not be remembered as broken for the rest of the TTL —
    /// that would fail every remaining chapter of the batch for the same stale reason.
    /// </summary>
    [Fact]
    public async Task A_failed_listing_is_not_cached()
    {
        var source = new CountingSource { Throws = new HttpRequestException("boom") };
        var cache = NewCache();

        await Assert.ThrowsAsync<HttpRequestException>(() => cache.GetAsync(source, "series-1", "en"));
        await Assert.ThrowsAsync<HttpRequestException>(() => cache.GetAsync(source, "series-1", "en"));
        Assert.Equal(2, source.ListCalls);

        source.Throws = null;
        var chapters = await cache.GetAsync(source, "series-1", "en");
        Assert.Single(chapters);
        Assert.Equal(3, source.ListCalls);
    }

    /// <summary>
    /// ChapterSyncService lists uncached and seeds the result here, so the enqueues a monitored
    /// refresh fires straight afterwards resolve against the listing that just found their chapter
    /// rather than a stale one that predates it.
    /// </summary>
    [Fact]
    public async Task A_stored_listing_is_served_without_going_to_the_source()
    {
        var source = new CountingSource();
        var cache = NewCache();
        var fresh = new SourceChapter[]
        {
            new(source.Name, "series-1", "99", "99", 99m, null, null, "en", null)
        };

        cache.Store(source, "series-1", "en", fresh);
        var served = await cache.GetAsync(source, "series-1", "en");

        Assert.Equal(0, source.ListCalls);
        Assert.Equal("99", Assert.Single(served).SourceChapterId);
    }

    [Fact]
    public async Task Store_replaces_an_already_cached_listing()
    {
        var source = new CountingSource();
        var cache = NewCache();

        await cache.GetAsync(source, "series-1", "en");
        cache.Store(source, "series-1", "en",
            [new(source.Name, "series-1", "99", "99", 99m, null, null, "en", null)]);

        var served = await cache.GetAsync(source, "series-1", "en");
        Assert.Equal(1, source.ListCalls);
        Assert.Equal("99", Assert.Single(served).SourceChapterId);
    }
}
