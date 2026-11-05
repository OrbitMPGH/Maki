using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Inbox;
using Maki.Core.Notifications;
using Maki.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// <see cref="Chapter.DelistedAt"/> as <see cref="ChapterSyncService"/> stamps and clears it: only a
/// chapter that loses its last live link is stamped, and only links that could mean something count.
/// </summary>
public class ChapterDelistedTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly RecordingInbox _inbox = new();

    public void Dispose() => _db.Dispose();

    private ChapterSyncService Service(params ISource[] sources) => Service(Sources.AllEnabled, sources);

    private ChapterSyncService Service(SourceAvailability availability, params ISource[] sources)
    {
        var registry = new SourceRegistry(sources);
        return new ChapterSyncService(
            _db.NewContext(),
            registry,
            new DownloadQueueService(null!, TimeProvider.System, null!, NullLogger<DownloadQueueService>.Instance),
            availability,
            new SourceChapterListCache(TimeProvider.System, NullLogger<SourceChapterListCache>.Instance),
            new FakeAppSettings(),
            NullLogger<ChapterSyncService>.Instance,
            inbox: _inbox);
    }

    private static FakeSource Listing(
        string name, Func<IEnumerable<SourceChapter>> chapters,
        SourceCapabilities capabilities = SourceCapabilities.None) =>
        new() { Name = name, Capabilities = capabilities, OnListChapters = _ => chapters().ToList() };

    private static FakeSource Listing(
        string name, List<decimal> numbers, SourceCapabilities capabilities = SourceCapabilities.None)
    {
        var maker = new FakeSource { Name = name };
        return Listing(name, () => numbers.Select(n => maker.Chapter(n)), capabilities);
    }

    private static SourceMapping Mapping(string source, string? languageFilter = null) => new()
    {
        SourceName = source,
        SourceSeriesId = "series",
        Url = $"https://{source}.test/series",
        Enabled = true,
        LanguageFilter = languageFilter
    };

    private Chapter ChapterOf(int seriesId, decimal number, string language = "en")
    {
        using var db = _db.NewContext();
        return db.Chapters.Single(c => c.SeriesId == seriesId && c.Number == number && c.Language == language);
    }

    private void Update(Action<Maki.Data.MakiDbContext> change)
    {
        using var db = _db.NewContext();
        change(db);
        db.SaveChanges();
    }

    private void GiveFile(int seriesId, decimal number)
    {
        using var db = _db.NewContext();
        var file = new ChapterFile { SeriesId = seriesId, RelativePath = $"{number}.cbz", DateAdded = DateTime.UtcNow };
        db.ChapterFiles.Add(file);
        db.SaveChanges();
        db.Chapters.Single(c => c.SeriesId == seriesId && c.Number == number).ChapterFileId = file.Id;
        db.SaveChanges();
    }

    [Fact]
    public async Task A_chapter_the_only_source_stops_listing_is_stamped()
    {
        var seriesId = _db.SeedSeries(mappings: Mapping("a"));
        List<decimal> listed = [1, 2, 3];
        var source = Listing("a", listed);

        await Service(source).SyncSeriesAsync(seriesId);
        listed.Remove(3);
        await Service(source).SyncSeriesAsync(seriesId);

        Assert.NotNull(ChapterOf(seriesId, 3).DelistedAt);
        Assert.Null(ChapterOf(seriesId, 1).DelistedAt);
        Assert.Null(ChapterOf(seriesId, 2).DelistedAt);
    }

    [Fact]
    public async Task A_chapter_another_source_still_lists_is_not_stamped_until_that_one_stops_too()
    {
        var seriesId = _db.SeedSeries(mappings: [Mapping("a"), Mapping("b")]);
        List<decimal> a = [1, 2, 3];
        List<decimal> b = [1, 2, 3];
        ISource[] sources = [Listing("a", a), Listing("b", b)];

        await Service(sources).SyncSeriesAsync(seriesId);
        a.Remove(3);
        await Service(sources).SyncSeriesAsync(seriesId);
        Assert.Null(ChapterOf(seriesId, 3).DelistedAt);

        b.Remove(3);
        await Service(sources).SyncSeriesAsync(seriesId);
        Assert.NotNull(ChapterOf(seriesId, 3).DelistedAt);
    }

    [Fact]
    public async Task A_chapter_one_source_drops_while_another_picks_it_up_in_the_same_sync_is_not_stamped()
    {
        var seriesId = _db.SeedSeries(mappings: [Mapping("a"), Mapping("b")]);
        List<decimal> a = [1, 2, 3];
        List<decimal> b = [1, 2];
        ISource[] sources = [Listing("a", a), Listing("b", b)];

        await Service(sources).SyncSeriesAsync(seriesId);
        a.Remove(3);
        b.Add(3);
        await Service(sources).SyncSeriesAsync(seriesId);

        Assert.Null(ChapterOf(seriesId, 3).DelistedAt);
    }

    [Fact]
    public async Task A_delisted_chapter_listed_again_is_cleared()
    {
        var seriesId = _db.SeedSeries(mappings: Mapping("a"));
        List<decimal> listed = [1, 2, 3];
        var source = Listing("a", listed);

        await Service(source).SyncSeriesAsync(seriesId);
        listed.Remove(3);
        await Service(source).SyncSeriesAsync(seriesId);
        listed.Add(3);
        await Service(source).SyncSeriesAsync(seriesId);

        Assert.Null(ChapterOf(seriesId, 3).DelistedAt);
    }

    [Fact]
    public async Task A_failing_or_empty_listing_stamps_nothing()
    {
        var seriesId = _db.SeedSeries(mappings: Mapping("a"));
        List<decimal> listed = [1, 2, 3];
        await Service(Listing("a", listed)).SyncSeriesAsync(seriesId);

        listed.Clear();
        await Service(Listing("a", listed)).SyncSeriesAsync(seriesId);
        await Service(new FakeSource { Name = "a", ListThrows = new HttpRequestException("down") }).SyncSeriesAsync(seriesId);

        Assert.All([1m, 2m, 3m], n => Assert.Null(ChapterOf(seriesId, n).DelistedAt));
    }

    [Fact]
    public async Task Links_on_a_disabled_mapping_neither_keep_a_chapter_listed_nor_get_it_stamped()
    {
        var seriesId = _db.SeedSeries(mappings: [Mapping("a"), Mapping("b")]);
        List<decimal> a = [1, 2, 3];
        List<decimal> b = [1, 2, 3, 4];
        ISource[] sources = [Listing("a", a), Listing("b", b)];
        await Service(sources).SyncSeriesAsync(seriesId);

        Update(db => db.SourceMappings.Single(m => m.SeriesId == seriesId && m.SourceName == "b").Enabled = false);
        a.Remove(3);
        await Service(sources).SyncSeriesAsync(seriesId);

        Assert.NotNull(ChapterOf(seriesId, 3).DelistedAt);
        Assert.Null(ChapterOf(seriesId, 4).DelistedAt);
    }

    [Fact]
    public async Task A_globally_switched_off_source_counts_like_a_disabled_mapping()
    {
        var seriesId = _db.SeedSeries(mappings: [Mapping("a"), Mapping("b")]);
        List<decimal> a = [1, 2, 3];
        List<decimal> b = [1, 2, 3, 4];
        ISource[] sources = [Listing("a", a), Listing("b", b)];
        await Service(sources).SyncSeriesAsync(seriesId);

        a.Remove(3);
        await Service(Sources.Disabled("b"), sources).SyncSeriesAsync(seriesId);

        Assert.NotNull(ChapterOf(seriesId, 3).DelistedAt);
        Assert.Null(ChapterOf(seriesId, 4).DelistedAt);
    }

    [Fact]
    public async Task A_chapter_no_source_ever_linked_is_never_stamped()
    {
        var seriesId = _db.SeedSeries(mappings: Mapping("a"));
        Update(db => db.Chapters.Add(new Chapter { SeriesId = seriesId, Number = 9m }));

        await Service(Listing("a", [1, 2])).SyncSeriesAsync(seriesId);
        await Service(Listing("a", [1, 2])).SyncSeriesAsync(seriesId);

        Assert.Null(ChapterOf(seriesId, 9).DelistedAt);
    }

    [Fact]
    public async Task A_free_window_source_dropping_a_chapter_does_not_stamp_it()
    {
        var seriesId = _db.SeedSeries(mappings: Mapping("official"));
        List<decimal> listed = [1, 2, 3];
        var source = Listing("official", listed, SourceCapabilities.FreeWindowListing);

        await Service(source).SyncSeriesAsync(seriesId);
        listed.Remove(1);
        await Service(source).SyncSeriesAsync(seriesId);

        Assert.Null(ChapterOf(seriesId, 1).DelistedAt);
    }

    [Fact]
    public async Task A_chapter_lost_from_a_free_window_source_and_an_ordinary_one_is_stamped()
    {
        var seriesId = _db.SeedSeries(mappings: [Mapping("official"), Mapping("a")]);
        List<decimal> official = [1, 2, 3];
        List<decimal> a = [1, 2, 3];
        ISource[] sources = [Listing("official", official, SourceCapabilities.FreeWindowListing), Listing("a", a)];

        await Service(sources).SyncSeriesAsync(seriesId);
        official.Remove(1);
        a.Remove(1);
        await Service(sources).SyncSeriesAsync(seriesId);

        Assert.NotNull(ChapterOf(seriesId, 1).DelistedAt);
    }

    [Fact]
    public async Task Dropping_a_language_from_the_filter_does_not_stamp_that_language()
    {
        var seriesId = _db.SeedSeries(mappings: Mapping("a", languageFilter: "en,es"));
        var maker = new FakeSource { Name = "a" };
        var languages = new List<string> { "en", "es" };
        var source = Listing("a", () => languages.SelectMany(l => new[] { maker.Chapter(1, language: l), maker.Chapter(2, language: l) }),
            SourceCapabilities.SupportsLanguageFilter);
        await Service(source).SyncSeriesAsync(seriesId);

        Update(db =>
        {
            var mapping = db.SourceMappings.Single(m => m.SeriesId == seriesId);
            mapping.LanguageFilter = "en";
            mapping.ChapterSnapshotAt = null;
        });
        languages.Remove("es");
        await Service(source).SyncSeriesAsync(seriesId);

        Assert.Null(ChapterOf(seriesId, 1, "es").DelistedAt);
        Assert.Null(ChapterOf(seriesId, 2, "es").DelistedAt);
    }

    [Fact]
    public async Task A_partial_sync_stamps_only_when_no_other_live_mapping_links_the_chapter()
    {
        var seriesId = _db.SeedSeries(mappings: [Mapping("a"), Mapping("b")]);
        List<decimal> a = [1, 2, 3, 4];
        List<decimal> b = [1, 2, 3];
        ISource[] sources = [Listing("a", a), Listing("b", b)];
        await Service(sources).SyncSeriesAsync(seriesId);
        int aId;
        using (var db = _db.NewContext())
        {
            aId = db.SourceMappings.Single(m => m.SeriesId == seriesId && m.SourceName == "a").Id;
        }

        a.Remove(3);
        a.Remove(4);
        await Service(sources).SyncMappingsAsync(seriesId, [aId]);

        Assert.Null(ChapterOf(seriesId, 3).DelistedAt);
        Assert.NotNull(ChapterOf(seriesId, 4).DelistedAt);
    }

    [Fact]
    public async Task A_merged_duplicate_carries_its_stamp_when_neither_row_is_linked()
    {
        var seriesId = _db.SeedSeries(mappings: Mapping("a"));
        var stamped = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        Update(db =>
        {
            db.Chapters.Add(new Chapter { SeriesId = seriesId, Number = 7m, Volume = 2 });
            db.Chapters.Add(new Chapter { SeriesId = seriesId, Number = 7m, DelistedAt = stamped });
        });

        await Service(Listing("a", [1])).SyncSeriesAsync(seriesId);

        Assert.Equal(stamped, ChapterOf(seriesId, 7).DelistedAt);
    }

    [Fact]
    public async Task A_cleared_chapter_with_no_links_is_not_stamped_again()
    {
        var seriesId = _db.SeedSeries(mappings: Mapping("a"));
        List<decimal> listed = [1, 2, 3];
        var source = Listing("a", listed);
        await Service(source).SyncSeriesAsync(seriesId);
        listed.Remove(3);
        await Service(source).SyncSeriesAsync(seriesId);

        Update(db => db.Chapters.Single(c => c.SeriesId == seriesId && c.Number == 3m).DelistedAt = null);
        await Service(source).SyncSeriesAsync(seriesId);

        Assert.Null(ChapterOf(seriesId, 3).DelistedAt);
    }

    [Fact]
    public async Task Stamping_downloaded_chapters_raises_one_warning_with_their_count()
    {
        var seriesId = _db.SeedSeries(mappings: Mapping("a"));
        List<decimal> listed = [1, 2, 3, 4];
        var source = Listing("a", listed);
        await Service(source).SyncSeriesAsync(seriesId);
        GiveFile(seriesId, 3);
        GiveFile(seriesId, 4);

        listed.RemoveAll(n => n >= 2);
        await Service(source).SyncSeriesAsync(seriesId);

        var (type, message, audience) = Assert.Single(_inbox.Raised);
        Assert.Equal(InboxEventType.ChapterDelisted, type);
        Assert.Equal("inbox.chapters.delisted", message.Key);
        Assert.Equal(NotificationLevel.Warning, message.Level);
        Assert.Equal(2, message.Params!["count"]);
        Assert.Equal(seriesId, message.SeriesId);
        Assert.Equal(InboxAudienceKind.SeriesTrackers, audience.Kind);

        await Service(source).SyncSeriesAsync(seriesId);
        Assert.Single(_inbox.Raised);
    }

    [Fact]
    public async Task Stamping_only_chapters_without_files_raises_nothing()
    {
        var seriesId = _db.SeedSeries(mappings: Mapping("a"));
        List<decimal> listed = [1, 2];
        var source = Listing("a", listed);
        await Service(source).SyncSeriesAsync(seriesId);

        listed.Remove(2);
        await Service(source).SyncSeriesAsync(seriesId);

        Assert.NotNull(ChapterOf(seriesId, 2).DelistedAt);
        Assert.Empty(_inbox.Raised);
    }
}
