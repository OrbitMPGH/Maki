using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// A newest-first source listed only up to its first page of known chapters: the monitored sync
/// merges such a listing (adds and updates links, removes none, caches nothing), while a full
/// listing still replaces the mapping's snapshot. The whole list comes back once a day.
/// </summary>
public sealed class ChapterSyncPartialListingTests : IDisposable
{
    private const int PageSize = 2;

    private readonly TestDb _db = new();
    private readonly Clock _clock = new();
    private readonly SourceChapterListCache _cache =
        new(TimeProvider.System, NullLogger<SourceChapterListCache>.Instance);

    public void Dispose() => _db.Dispose();

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Pages its list newest first and stops like MangaFire's pager does.</summary>
    private sealed class PagedSource : ISource, INewestFirstChapterSource
    {
        public string Name => "paged";
        public string DisplayName => Name;
        public string BaseUrl => "https://paged.test";
        public SourceCapabilities Capabilities => SourceCapabilities.None;
        public IReadOnlyList<string> SupportedLanguages => ["en"];

        public List<SourceChapter> Listed { get; } = [];
        public int FullCalls { get; private set; }
        public int PartialCalls { get; private set; }
        public int PagesRead { get; private set; }

        public static SourceChapter Chapter(decimal number, string? id = null) =>
            new("paged", "series", id ?? $"id-{number}", number.ToString(), number, null, null, "en", null);

        public Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SourceSeriesResult>>([]);

        public Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default) =>
            throw new NotSupportedException();

        private List<SourceChapter> NewestFirst() => Listed.OrderByDescending(c => c.Number).ToList();

        public Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
            string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
        {
            FullCalls++;
            PagesRead += (Listed.Count + PageSize - 1) / PageSize;
            return Task.FromResult<IReadOnlyList<SourceChapter>>(NewestFirst());
        }

        public Task<ChapterListing> ListNewChaptersAsync(
            string sourceSeriesId, string? languageFilter, KnownChapters known, CancellationToken ct = default)
        {
            PartialCalls++;
            var all = NewestFirst();
            var taken = new List<SourceChapter>();
            for (var start = 0; start < all.Count; start += PageSize)
            {
                var page = all.Skip(start).Take(PageSize).ToList();
                taken.AddRange(page);
                PagesRead++;
                if (known.CoversPage(page) && start + PageSize < all.Count)
                {
                    return Task.FromResult(new ChapterListing(taken, Complete: false));
                }
            }

            return Task.FromResult(new ChapterListing(taken, Complete: true));
        }
    }

    private ChapterSyncService Service(PagedSource source) =>
        new(
            _db.NewContext(),
            new SourceRegistry([source]),
            new DownloadQueueService(null!, TimeProvider.System, null!, NullLogger<DownloadQueueService>.Instance),
            Sources.AllEnabled,
            _cache,
            new FakeAppSettings(),
            NullLogger<ChapterSyncService>.Instance,
            listingSchedule: _schedule);

    private readonly ChapterListingSchedule _schedule;

    public ChapterSyncPartialListingTests() => _schedule = new ChapterListingSchedule(_clock);

    private int SeedSeries() => _db.SeedSeries(mappings: new SourceMapping
    {
        SourceName = "paged", SourceSeriesId = "series", Url = "https://paged.test/series", Enabled = true,
    });

    private List<(decimal? Number, string SourceChapterId)> LinksOf(int seriesId)
    {
        using var db = _db.NewContext();
        return db.ChapterSourceLinks
            .Where(l => l.Chapter!.SeriesId == seriesId)
            .Select(l => new { l.Chapter!.Number, l.SourceChapterId })
            .AsEnumerable()
            .Select(l => (l.Number, l.SourceChapterId))
            .OrderBy(l => l.Number)
            .ToList();
    }

    private async Task<PagedSource> SyncedSourceAsync(int seriesId, int chapters = 8)
    {
        var source = new PagedSource();
        source.Listed.AddRange(Enumerable.Range(1, chapters).Select(n => PagedSource.Chapter(n)));
        await Service(source).SyncSeriesAsync(seriesId, ChapterListingDepth.NewestFirst);
        Assert.Equal(1, source.FullCalls);
        Assert.Equal(0, source.PartialCalls);
        return source;
    }

    [Fact]
    public async Task The_first_sync_walks_the_whole_list()
    {
        var seriesId = SeedSeries();

        var source = await SyncedSourceAsync(seriesId);

        Assert.Equal(8, LinksOf(seriesId).Count);
        Assert.Equal(4, source.PagesRead);
    }

    [Fact]
    public async Task A_later_monitored_sync_reads_only_the_newest_pages_and_merges_the_new_chapter()
    {
        var seriesId = SeedSeries();
        var source = await SyncedSourceAsync(seriesId);
        source.Listed.Add(PagedSource.Chapter(9));
        var pagesBefore = source.PagesRead;

        var added = await Service(source).SyncSeriesAsync(seriesId, ChapterListingDepth.NewestFirst);

        Assert.Equal(1, source.PartialCalls);
        Assert.Equal(1, source.FullCalls);
        Assert.Equal(2, source.PagesRead - pagesBefore);
        Assert.Single(added);
        Assert.Equal(9, LinksOf(seriesId).Count);
    }

    [Fact]
    public async Task A_partial_listing_delists_nothing_and_the_next_whole_walk_does()
    {
        var seriesId = SeedSeries();
        var source = await SyncedSourceAsync(seriesId);
        source.Listed.RemoveAll(c => c.Number <= 2);

        await Service(source).SyncSeriesAsync(seriesId, ChapterListingDepth.NewestFirst);
        Assert.Equal(1, source.PartialCalls);
        Assert.Empty(DelistedOf(seriesId));

        await Service(source).SyncSeriesAsync(seriesId);
        Assert.Equal([1m, 2m], DelistedOf(seriesId));
    }

    private List<decimal?> DelistedOf(int seriesId)
    {
        using var db = _db.NewContext();
        return db.Chapters.Where(c => c.SeriesId == seriesId && c.DelistedAt != null)
            .Select(c => c.Number).AsEnumerable().OrderBy(n => n).ToList();
    }

    [Fact]
    public async Task A_partial_listing_removes_no_link_and_a_full_one_does()
    {
        var seriesId = SeedSeries();
        var source = await SyncedSourceAsync(seriesId);
        source.Listed.RemoveAll(c => c.Number == 1);

        await Service(source).SyncSeriesAsync(seriesId, ChapterListingDepth.NewestFirst);
        Assert.Equal(1, source.PartialCalls);
        Assert.Contains(LinksOf(seriesId), l => l.Number == 1);

        await Service(source).SyncSeriesAsync(seriesId);
        Assert.Equal(2, source.FullCalls);
        Assert.DoesNotContain(LinksOf(seriesId), l => l.Number == 1);
    }

    [Fact]
    public async Task A_partial_listing_updates_the_id_of_a_reuploaded_chapter_it_reaches()
    {
        var seriesId = SeedSeries();
        var source = await SyncedSourceAsync(seriesId);
        source.Listed.RemoveAll(c => c.Number == 8);
        source.Listed.Add(PagedSource.Chapter(8, id: "reupload-8"));

        await Service(source).SyncSeriesAsync(seriesId, ChapterListingDepth.NewestFirst);

        Assert.Equal(1, source.PartialCalls);
        Assert.Contains(LinksOf(seriesId), l => l is { Number: 8, SourceChapterId: "reupload-8" });
    }

    [Fact]
    public async Task A_partial_listing_is_not_cached_as_the_whole_list()
    {
        var seriesId = SeedSeries();
        var source = await SyncedSourceAsync(seriesId);
        source.Listed.Add(PagedSource.Chapter(9));

        await Service(source).SyncSeriesAsync(seriesId, ChapterListingDepth.NewestFirst);

        var cached = _cache.TryGetFresh(source, "series", null);
        Assert.NotNull(cached);
        Assert.Equal(8, cached.Count);
    }

    [Fact]
    public async Task The_whole_list_is_walked_again_after_a_day()
    {
        var seriesId = SeedSeries();
        var source = await SyncedSourceAsync(seriesId);

        _clock.Now += ChapterListingSchedule.FullEvery - TimeSpan.FromMinutes(1);
        await Service(source).SyncSeriesAsync(seriesId, ChapterListingDepth.NewestFirst);
        Assert.Equal(1, source.PartialCalls);

        _clock.Now += TimeSpan.FromMinutes(1);
        await Service(source).SyncSeriesAsync(seriesId, ChapterListingDepth.NewestFirst);
        Assert.Equal(2, source.FullCalls);
    }

    [Fact]
    public async Task A_manual_refresh_always_walks_the_whole_list()
    {
        var seriesId = SeedSeries();
        var source = await SyncedSourceAsync(seriesId);

        await Service(source).SyncSeriesAsync(seriesId);

        Assert.Equal(2, source.FullCalls);
        Assert.Equal(0, source.PartialCalls);
    }

    [Fact]
    public async Task A_new_chapter_found_by_a_partial_listing_is_stamped_as_an_arrival()
    {
        var seriesId = SeedSeries();
        var source = await SyncedSourceAsync(seriesId);
        source.Listed.Add(PagedSource.Chapter(9));

        await Service(source).SyncSeriesAsync(seriesId, ChapterListingDepth.NewestFirst);

        using var db = _db.NewContext();
        var links = db.ChapterSourceLinks.Where(l => l.Chapter!.SeriesId == seriesId)
            .Select(l => new { l.Chapter!.Number, l.FirstSeenAt }).ToList();
        Assert.NotNull(links.Single(l => l.Number == 9).FirstSeenAt);
        Assert.All(links.Where(l => l.Number != 9), l => Assert.Null(l.FirstSeenAt));
    }

    [Fact]
    public async Task A_partial_listing_does_not_clear_a_numbering_clash()
    {
        var seriesId = SeedSeries();
        var source = await SyncedSourceAsync(seriesId);
        using (var db = _db.NewContext())
        {
            db.Series.Single(s => s.Id == seriesId).NumberingClash = "a|b";
            db.SaveChanges();
        }

        await Service(source).SyncSeriesAsync(seriesId, ChapterListingDepth.NewestFirst);
        using (var db = _db.NewContext())
        {
            Assert.Equal("a|b", db.Series.Single(s => s.Id == seriesId).NumberingClash);
        }

        await Service(source).SyncSeriesAsync(seriesId);
        using (var db = _db.NewContext())
        {
            Assert.Null(db.Series.Single(s => s.Id == seriesId).NumberingClash);
        }
    }

    [Fact]
    public async Task A_restart_walks_the_whole_list_first()
    {
        var seriesId = SeedSeries();
        var source = await SyncedSourceAsync(seriesId);
        var restarted = new ChapterListingSchedule(_clock);

        await new ChapterSyncService(
            _db.NewContext(), new SourceRegistry([source]),
            new DownloadQueueService(null!, TimeProvider.System, null!, NullLogger<DownloadQueueService>.Instance),
            Sources.AllEnabled, _cache, new FakeAppSettings(), NullLogger<ChapterSyncService>.Instance,
            listingSchedule: restarted).SyncSeriesAsync(seriesId, ChapterListingDepth.NewestFirst);

        Assert.Equal(2, source.FullCalls);
    }

    [Fact]
    public async Task The_resolver_uses_the_stored_link_of_a_newest_first_source_before_walking_its_list()
    {
        var seriesId = SeedSeries();
        var source = await SyncedSourceAsync(seriesId);
        source.Listed.Add(PagedSource.Chapter(9));
        await Service(source).SyncSeriesAsync(seriesId, ChapterListingDepth.NewestFirst);
        var fullCalls = source.FullCalls;

        using var db = _db.NewContext();
        var chapter = db.Chapters.Single(c => c.SeriesId == seriesId && c.Number == 9);
        var resolver = Sources.Resolver(new SourceRegistry([source]), listingSchedule: _schedule);

        var resolved = await resolver.ResolveAsync(db, chapter, null, CancellationToken.None);

        Assert.Equal("id-9", resolved.SourceChapterId);
        Assert.Equal(fullCalls, source.FullCalls);
    }

    /// <summary>
    /// The processor's 404 path never lists again, so a stored id is only trusted while the mapping
    /// had a whole walk recently; after a restart (or on a mapping nothing syncs) the list is walked.
    /// </summary>
    [Fact]
    public async Task The_resolver_walks_the_list_when_the_mapping_had_no_recent_whole_walk()
    {
        var seriesId = SeedSeries();
        var source = await SyncedSourceAsync(seriesId);
        var fullCalls = source.FullCalls;

        using var db = _db.NewContext();
        var chapter = db.Chapters.First(c => c.SeriesId == seriesId && c.Number != null);
        var resolver = Sources.Resolver(new SourceRegistry([source]), listingSchedule: new ChapterListingSchedule(_clock));

        await resolver.ResolveAsync(db, chapter, null, CancellationToken.None);

        Assert.Equal(fullCalls + 1, source.FullCalls);
    }
}
