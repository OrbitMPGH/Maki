using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Http;
using Maki.Core.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// Drives <see cref="ChapterSyncService.SyncSeriesAsync"/> against an in-memory DB and
/// canned sources — covering insert, enrich-not-duplicate, duplicate healing, one-shot
/// matching, monitor modes, rate-limit backoff, clash detection and uuid backfill.
/// </summary>
public class ChapterSyncServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private ChapterSyncService BuildService(DownloadQueueService? queue, params ISource[] sources) =>
        new(
            _db.NewContext(),
            new SourceRegistry(sources),
            queue ?? new DownloadQueueService(null!, TimeProvider.System),
            NullLogger<ChapterSyncService>.Instance);

    private static SourceMapping Mapping(string source, bool enabled = true) => new()
    {
        SourceName = source,
        SourceSeriesId = "series",
        Url = $"https://{source}.test/series",
        Enabled = enabled
    };

    private List<Chapter> ChaptersOf(int seriesId)
    {
        using var db = _db.NewContext();
        return db.Chapters.Where(c => c.SeriesId == seriesId).OrderBy(c => c.Id).ToList();
    }

    [Fact]
    public async Task New_chapters_are_inserted_and_returned()
    {
        var seriesId = _db.SeedSeries(mappings: Mapping("fake"));
        var source = new FakeSource
        {
            Name = "fake",
            OnListChapters = _ => [new FakeSource { Name = "fake" }.Chapter(1), new FakeSource { Name = "fake" }.Chapter(2)]
        };

        var newIds = await BuildService(null, source).SyncSeriesAsync(seriesId);

        Assert.Equal(2, newIds.Count);
        var chapters = ChaptersOf(seriesId);
        Assert.Equal([1m, 2m], chapters.Select(c => c.Number));
        Assert.All(chapters, c => Assert.True(c.Monitored));
    }

    [Fact]
    public async Task Existing_chapter_is_enriched_not_duplicated()
    {
        var seriesId = _db.SeedSeries(mappings: Mapping("fake"));
        using (var db = _db.NewContext())
        {
            db.Chapters.Add(new Chapter { SeriesId = seriesId, Number = 1m, Language = "en" });
            db.SaveChanges();
        }

        var fake = new FakeSource { Name = "fake" };
        var source = new FakeSource
        {
            Name = "fake",
            OnListChapters = _ => [fake.Chapter(1, volume: 3, title: "The Start", releaseDate: new DateTime(2020, 5, 1))]
        };

        var newIds = await BuildService(null, source).SyncSeriesAsync(seriesId);

        Assert.Empty(newIds);
        var chapter = Assert.Single(ChaptersOf(seriesId));
        Assert.Equal(3, chapter.Volume);
        Assert.Equal("The Start", chapter.Title);
        Assert.Equal(new DateTime(2020, 5, 1), chapter.ReleaseDate);
    }

    [Fact]
    public async Task Volume_wildcard_matches_a_volumeless_existing_chapter()
    {
        var seriesId = _db.SeedSeries(mappings: Mapping("fake"));
        using (var db = _db.NewContext())
        {
            db.Chapters.Add(new Chapter { SeriesId = seriesId, Number = 27m, Volume = null, Language = "en" });
            db.SaveChanges();
        }

        var fake = new FakeSource { Name = "fake" };
        var source = new FakeSource { Name = "fake", OnListChapters = _ => [fake.Chapter(27, volume: 4)] };

        var newIds = await BuildService(null, source).SyncSeriesAsync(seriesId);

        Assert.Empty(newIds);
        Assert.Equal(4, Assert.Single(ChaptersOf(seriesId)).Volume);
    }

    [Fact]
    public async Task Duplicate_rows_are_healed_keeping_the_richest_copy()
    {
        var seriesId = _db.SeedSeries(mappings: Mapping("fake"));
        using (var db = _db.NewContext())
        {
            db.Chapters.Add(new Chapter { SeriesId = seriesId, Number = 5m, Volume = null, Language = "en" });
            db.Chapters.Add(new Chapter { SeriesId = seriesId, Number = 5m, Volume = 2, Title = "Vol copy", Language = "en" });
            db.SaveChanges();
        }

        // Empty source list — the heal runs before the source loop.
        var source = new FakeSource { Name = "fake", OnListChapters = _ => [] };
        await BuildService(null, source).SyncSeriesAsync(seriesId);

        var chapter = Assert.Single(ChaptersOf(seriesId));
        Assert.Equal(2, chapter.Volume);
        Assert.Equal("Vol copy", chapter.Title);
    }

    [Fact]
    public async Task Distinct_explicit_volumes_are_not_treated_as_duplicates()
    {
        var seriesId = _db.SeedSeries(mappings: Mapping("fake"));
        using (var db = _db.NewContext())
        {
            db.Chapters.Add(new Chapter { SeriesId = seriesId, Number = 1m, Volume = 1, Language = "en" });
            db.Chapters.Add(new Chapter { SeriesId = seriesId, Number = 1m, Volume = 2, Language = "en" });
            db.SaveChanges();
        }

        var source = new FakeSource { Name = "fake", OnListChapters = _ => [] };
        await BuildService(null, source).SyncSeriesAsync(seriesId);

        Assert.Equal(2, ChaptersOf(seriesId).Count);
    }

    [Fact]
    public async Task One_shot_matches_by_title_case_insensitive()
    {
        var seriesId = _db.SeedSeries(mappings: Mapping("fake"));
        using (var db = _db.NewContext())
        {
            db.Chapters.Add(new Chapter
            {
                SeriesId = seriesId, Number = null, IsOneShot = true, Title = "Omake", Language = "en"
            });
            db.SaveChanges();
        }

        var fake = new FakeSource { Name = "fake" };
        var source = new FakeSource { Name = "fake", OnListChapters = _ => [fake.Chapter(null, title: "omake")] };

        var newIds = await BuildService(null, source).SyncSeriesAsync(seriesId);

        Assert.Empty(newIds);
        Assert.Single(ChaptersOf(seriesId));
    }

    [Fact]
    public async Task MainOnly_mode_leaves_specials_unmonitored()
    {
        var seriesId = _db.SeedSeries(monitor: NewChapterMonitorMode.MainOnly, mappings: Mapping("fake"));
        var fake = new FakeSource { Name = "fake" };
        var source = new FakeSource { Name = "fake", OnListChapters = _ => [fake.Chapter(10), fake.Chapter(10.5m)] };

        await BuildService(null, source).SyncSeriesAsync(seriesId);

        var chapters = ChaptersOf(seriesId);
        Assert.True(chapters.Single(c => c.Number == 10m).Monitored);
        Assert.False(chapters.Single(c => c.Number == 10.5m).Monitored);
    }

    [Fact]
    public async Task Disabled_mapping_is_never_queried()
    {
        var seriesId = _db.SeedSeries(mappings: Mapping("fake", enabled: false));
        var fake = new FakeSource { Name = "fake" };
        var source = new FakeSource { Name = "fake", OnListChapters = _ => [fake.Chapter(1)] };

        var newIds = await BuildService(null, source).SyncSeriesAsync(seriesId);

        Assert.Empty(newIds);
        Assert.Equal(0, source.ListCalls);
        Assert.Empty(ChaptersOf(seriesId));
    }

    [Fact]
    public async Task Rate_limit_backs_off_the_queue_and_records_the_error()
    {
        var seriesId = _db.SeedSeries(mappings: Mapping("fake"));
        var queue = new DownloadQueueService(null!, TimeProvider.System);
        var source = new FakeSource
        {
            Name = "fake",
            ListThrows = new RateLimitException("429", TimeSpan.FromMinutes(2))
        };

        var newIds = await BuildService(queue, source).SyncSeriesAsync(seriesId);

        Assert.Empty(newIds);
        Assert.True(queue.CooldownRemaining() > TimeSpan.Zero);
        using var db = _db.NewContext();
        var mapping = db.SourceMappings.Single(m => m.SeriesId == seriesId);
        Assert.NotNull(mapping.LastError);
        Assert.Contains("paused", mapping.LastError);
    }

    [Fact]
    public async Task Ordinary_source_failure_is_recorded_without_touching_the_queue()
    {
        var seriesId = _db.SeedSeries(mappings: Mapping("fake"));
        var queue = new DownloadQueueService(null!, TimeProvider.System);
        var source = new FakeSource { Name = "fake", ListThrows = new InvalidOperationException("boom") };

        var newIds = await BuildService(queue, source).SyncSeriesAsync(seriesId);

        Assert.Empty(newIds);
        Assert.Equal(TimeSpan.Zero, queue.CooldownRemaining());
        using var db = _db.NewContext();
        Assert.Equal("boom", db.SourceMappings.Single(m => m.SeriesId == seriesId).LastError);
    }

    [Fact]
    public async Task Cross_source_numbering_clash_is_flagged()
    {
        var seriesId = _db.SeedSeries(mappings: [Mapping("sub"), Mapping("whole")]);
        var sub = new FakeSource
        {
            Name = "sub",
            OnListChapters = _ => [new FakeSource { Name = "sub" }.Chapter(1.1m),
                new FakeSource { Name = "sub" }.Chapter(2.1m), new FakeSource { Name = "sub" }.Chapter(3.1m)]
        };
        var whole = new FakeSource
        {
            Name = "whole",
            OnListChapters = _ => [new FakeSource { Name = "whole" }.Chapter(1),
                new FakeSource { Name = "whole" }.Chapter(2), new FakeSource { Name = "whole" }.Chapter(3)]
        };

        await BuildService(null, sub, whole).SyncSeriesAsync(seriesId);

        using var db = _db.NewContext();
        Assert.Equal("sub|whole", db.Series.Single(s => s.Id == seriesId).NumberingClash);
    }

    [Fact]
    public async Task MangaDex_uuid_is_backfilled_from_the_mapping()
    {
        var seriesId = _db.SeedSeries(mappings: new SourceMapping
        {
            SourceName = "mangadex",
            SourceSeriesId = "uuid-123",
            Url = "https://mangadex.test/series",
            Enabled = true
        });
        var source = new FakeSource { Name = "mangadex", OnListChapters = _ => [] };

        await BuildService(null, source).SyncSeriesAsync(seriesId);

        using var db = _db.NewContext();
        Assert.Equal("uuid-123", db.Series.Single(s => s.Id == seriesId).MangaDexUuid);
    }
}
