using Maki.Api.Services;
using Maki.Core.Configuration;
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
        BuildService(Sources.AllEnabled, queue, sources);

    private ChapterSyncService BuildService(
        SourceAvailability availability, DownloadQueueService? queue, params ISource[] sources) =>
        BuildService(availability, queue, new FakeAppSettings(), sources);

    private ChapterSyncService BuildService(
        SourceAvailability availability, DownloadQueueService? queue, IAppSettings settings, params ISource[] sources) =>
        new(
            _db.NewContext(),
            new SourceRegistry(sources),
            queue ?? new DownloadQueueService(null!, TimeProvider.System, null!, NullLogger<DownloadQueueService>.Instance),
            availability,
            new SourceChapterListCache(TimeProvider.System, NullLogger<SourceChapterListCache>.Instance),
            settings,
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

    private List<ChapterSourceLink> LinksOf(int seriesId)
    {
        using var db = _db.NewContext();
        return db.ChapterSourceLinks
            .Where(l => l.Chapter!.SeriesId == seriesId)
            .OrderBy(l => l.ChapterId)
            .ToList();
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
        Assert.All(chapters, c => Assert.True(c.Wanted));

        using var db = _db.NewContext();
        Assert.NotNull(db.SourceMappings.Single(m => m.SeriesId == seriesId).ChapterSnapshotAt);
        Assert.Equal(2, db.ChapterSourceLinks.Count());
    }

    [Fact]
    public async Task Successful_refresh_replaces_only_the_mapping_snapshot()
    {
        var seriesId = _db.SeedSeries(mappings: Mapping("fake"));
        var listed = new List<SourceChapter>();
        var fake = new FakeSource { Name = "fake" };
        var source = new FakeSource { Name = "fake", OnListChapters = _ => listed };
        listed.AddRange([fake.Chapter(1, title: "Old"), fake.Chapter(2)]);

        await BuildService(null, source).SyncSeriesAsync(seriesId);
        listed.Clear();
        listed.Add(fake.Chapter(2, title: "Still listed"));
        await BuildService(null, source).SyncSeriesAsync(seriesId);

        // Refresh stays additive, but the source snapshot mirrors its last successful listing.
        Assert.Equal(2, ChaptersOf(seriesId).Count);
        var link = Assert.Single(LinksOf(seriesId));
        Assert.Equal(2m, ChaptersOf(seriesId).Single(c => c.Id == link.ChapterId).Number);
        Assert.Equal("Still listed", link.Title);
    }

    [Fact]
    public async Task Failed_refresh_preserves_the_last_successful_snapshot()
    {
        var seriesId = _db.SeedSeries(mappings: Mapping("fake"));
        var fake = new FakeSource { Name = "fake" };
        await BuildService(null, new FakeSource
        {
            Name = "fake", OnListChapters = _ => [fake.Chapter(1, title: "Known")]
        }).SyncSeriesAsync(seriesId);

        DateTime? snapshotAt;
        using (var db = _db.NewContext())
        {
            snapshotAt = db.SourceMappings.Single(m => m.SeriesId == seriesId).ChapterSnapshotAt;
        }

        await BuildService(null, new FakeSource
        {
            Name = "fake", ListThrows = new InvalidOperationException("offline")
        }).SyncSeriesAsync(seriesId);

        using var check = _db.NewContext();
        Assert.Equal(snapshotAt, check.SourceMappings.Single(m => m.SeriesId == seriesId).ChapterSnapshotAt);
        Assert.Equal("Known", Assert.Single(check.ChapterSourceLinks).Title);
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
    public async Task Duplicate_merge_transfers_source_snapshot_links_to_the_keeper()
    {
        var seriesId = _db.SeedSeries(mappings: Mapping("fake", enabled: false));
        int keeperId;
        using (var db = _db.NewContext())
        {
            var mappingId = db.SourceMappings.Single(m => m.SeriesId == seriesId).Id;
            var keeper = new Chapter
            {
                SeriesId = seriesId, Number = 5, Volume = 2, Title = "Rich", Language = "en"
            };
            var duplicate = new Chapter { SeriesId = seriesId, Number = 5, Language = "en" };
            db.Chapters.AddRange(keeper, duplicate);
            db.SaveChanges();
            keeperId = keeper.Id;
            db.ChapterSourceLinks.Add(new ChapterSourceLink
            {
                ChapterId = duplicate.Id,
                SourceMappingId = mappingId,
                SourceChapterId = "source-5",
                Title = "Source title"
            });
            db.SaveChanges();
        }

        await BuildService(null, new FakeSource { Name = "fake" }).SyncSeriesAsync(seriesId);

        Assert.Equal(keeperId, Assert.Single(ChaptersOf(seriesId)).Id);
        Assert.Equal(keeperId, Assert.Single(LinksOf(seriesId)).ChapterId);
    }

    [Fact]
    public async Task Duplicate_merge_keeps_the_scanlation_group_on_the_moved_link()
    {
        var seriesId = _db.SeedSeries(mappings: Mapping("fake", enabled: false));
        using (var db = _db.NewContext())
        {
            var mappingId = db.SourceMappings.Single(m => m.SeriesId == seriesId).Id;
            var keeper = new Chapter { SeriesId = seriesId, Number = 5, Volume = 2, Language = "en" };
            var duplicate = new Chapter { SeriesId = seriesId, Number = 5, Language = "en" };
            db.Chapters.AddRange(keeper, duplicate);
            db.SaveChanges();
            db.ChapterSourceLinks.Add(new ChapterSourceLink
            {
                ChapterId = duplicate.Id,
                SourceMappingId = mappingId,
                SourceChapterId = "source-5",
                Group = "Night Scans"
            });
            db.SaveChanges();
        }

        await BuildService(null, new FakeSource { Name = "fake" }).SyncSeriesAsync(seriesId);

        Assert.Equal("Night Scans", Assert.Single(LinksOf(seriesId)).Group);
    }

    [Fact]
    public async Task A_file_from_the_same_source_with_no_group_takes_the_links_group()
    {
        var seriesId = _db.SeedSeries(mappings: Mapping("fake"));
        int sameSource, otherSource, alreadyNamed;
        using (var db = _db.NewContext())
        {
            ChapterFile NewFile(string name, string source, string? group = null) => new()
            {
                SeriesId = seriesId, RelativePath = name, SourceName = source, Group = group, DateAdded = DateTime.UtcNow
            };

            var same = NewFile("1.cbz", "fake");
            var other = NewFile("2.cbz", "import");
            var named = NewFile("3.cbz", "fake", "Old Group");
            db.ChapterFiles.AddRange(same, other, named);
            db.SaveChanges();
            db.Chapters.AddRange(
                new Chapter { SeriesId = seriesId, Number = 1, Language = "en", ChapterFileId = same.Id },
                new Chapter { SeriesId = seriesId, Number = 2, Language = "en", ChapterFileId = other.Id },
                new Chapter { SeriesId = seriesId, Number = 3, Language = "en", ChapterFileId = named.Id });
            db.SaveChanges();
            (sameSource, otherSource, alreadyNamed) = (same.Id, other.Id, named.Id);
        }

        var fake = new FakeSource { Name = "fake" };
        var source = new FakeSource
        {
            Name = "fake",
            OnListChapters = _ =>
            [
                fake.Chapter(1) with { Group = "Night Scans" },
                fake.Chapter(2) with { Group = "Night Scans" },
                fake.Chapter(3) with { Group = "Night Scans" }
            ]
        };

        await BuildService(null, source).SyncSeriesAsync(seriesId);

        using var check = _db.NewContext();
        Assert.Equal("Night Scans", check.ChapterFiles.Single(f => f.Id == sameSource).Group);
        Assert.Null(check.ChapterFiles.Single(f => f.Id == otherSource).Group);
        Assert.Equal("Old Group", check.ChapterFiles.Single(f => f.Id == alreadyNamed).Group);
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
    public async Task One_shot_titled_by_a_now_numbered_label_is_promoted_in_place()
    {
        var seriesId = _db.SeedSeries(mappings: Mapping("fake"));
        int fileId;
        using (var db = _db.NewContext())
        {
            var file = new ChapterFile { SeriesId = seriesId, RelativePath = "Series/Episode 124.cbz" };
            db.ChapterFiles.Add(file);
            db.SaveChanges();
            fileId = file.Id;
            db.Chapters.Add(new Chapter
            {
                SeriesId = seriesId, Number = null, IsOneShot = true, Title = "Episode 124",
                Language = "en", ChapterFileId = fileId
            });
            db.SaveChanges();
        }

        var fake = new FakeSource { Name = "fake" };
        var source = new FakeSource
        {
            Name = "fake",
            OnListChapters = _ => [fake.Chapter(124) with { NumberRaw = "Episode 124" }]
        };

        var newIds = await BuildService(null, source).SyncSeriesAsync(seriesId);

        Assert.Empty(newIds);
        var chapter = Assert.Single(ChaptersOf(seriesId));
        Assert.Equal(124m, chapter.Number);
        Assert.False(chapter.IsOneShot);
        Assert.Null(chapter.Title);
        Assert.Equal(fileId, chapter.ChapterFileId);
    }

    [Fact]
    public async Task One_shot_titled_by_a_now_numbered_label_merges_into_the_existing_number()
    {
        var seriesId = _db.SeedSeries(mappings: [Mapping("fake"), Mapping("other")]);
        int keeperId;
        using (var db = _db.NewContext())
        {
            var numbered = new Chapter { SeriesId = seriesId, Number = 224, Volume = 17, Language = "en" };
            db.Chapters.AddRange(
                numbered,
                new Chapter
                {
                    SeriesId = seriesId, Number = null, IsOneShot = true, Title = "Anna-chan Can't Study",
                    Language = "en"
                });
            db.SaveChanges();
            keeperId = numbered.Id;
        }

        var fake = new FakeSource { Name = "fake" };
        var source = new FakeSource
        {
            Name = "fake",
            OnListChapters = _ =>
                [fake.Chapter(224) with { NumberRaw = "Anna-chan Can't Study", Title = "Anna-chan Can't Study" }]
        };
        var other = new FakeSource { Name = "other", OnListChapters = _ => [fake.Chapter(224) with { SourceName = "other" }] };

        var newIds = await BuildService(null, source, other).SyncSeriesAsync(seriesId);

        Assert.Empty(newIds);
        var chapter = Assert.Single(ChaptersOf(seriesId));
        Assert.Equal(keeperId, chapter.Id);
        Assert.Equal(224m, chapter.Number);
        Assert.Equal("Anna-chan Can't Study", chapter.Title);
        Assert.All(LinksOf(seriesId), l => Assert.Equal(keeperId, l.ChapterId));
    }

    [Fact]
    public async Task Untitled_unnumbered_chapters_with_different_labels_stay_distinct()
    {
        var seriesId = _db.SeedSeries(mappings: Mapping("fake"));
        var fake = new FakeSource { Name = "fake" };
        var source = new FakeSource
        {
            Name = "fake",
            OnListChapters = _ =>
            [
                fake.Chapter(null) with { SourceChapterId = "a", NumberRaw = "Special" },
                fake.Chapter(null) with { SourceChapterId = "b", NumberRaw = "Extra" },
            ]
        };

        await BuildService(null, source).SyncSeriesAsync(seriesId);
        var newIds = await BuildService(null, source).SyncSeriesAsync(seriesId);

        Assert.Empty(newIds);
        var chapters = ChaptersOf(seriesId);
        Assert.Equal(["Special", "Extra"], chapters.Select(c => c.Title));
        Assert.All(chapters, c => Assert.True(c.IsOneShot));
        Assert.Equal(2, LinksOf(seriesId).Count);
    }

    [Fact]
    public async Task Labelled_one_shot_is_promoted_once_its_label_parses()
    {
        var seriesId = _db.SeedSeries(mappings: Mapping("fake"));
        var fake = new FakeSource { Name = "fake" };
        var listed = new List<SourceChapter> { fake.Chapter(null) with { NumberRaw = "Episode 7" } };
        var source = new FakeSource { Name = "fake", OnListChapters = _ => listed };

        await BuildService(null, source).SyncSeriesAsync(seriesId);
        listed[0] = fake.Chapter(7) with { NumberRaw = "Episode 7" };
        var newIds = await BuildService(null, source).SyncSeriesAsync(seriesId);

        Assert.Empty(newIds);
        var chapter = Assert.Single(ChaptersOf(seriesId));
        Assert.Equal(7m, chapter.Number);
        Assert.False(chapter.IsOneShot);
    }

    [Fact]
    public async Task Untitled_one_shot_from_before_labelling_is_adopted_not_duplicated()
    {
        var seriesId = _db.SeedSeries(mappings: Mapping("fake"));
        using (var db = _db.NewContext())
        {
            db.Chapters.Add(new Chapter { SeriesId = seriesId, Number = null, IsOneShot = true, Language = "en" });
            db.SaveChanges();
        }

        var fake = new FakeSource { Name = "fake" };
        var source = new FakeSource
        {
            Name = "fake",
            OnListChapters = _ => [fake.Chapter(null) with { SourceChapterId = "a", NumberRaw = "Special" }]
        };

        var newIds = await BuildService(null, source).SyncSeriesAsync(seriesId);

        Assert.Empty(newIds);
        Assert.Equal("Special", Assert.Single(ChaptersOf(seriesId)).Title);
    }

    [Fact]
    public async Task Empty_listing_keeps_existing_links_and_records_an_error()
    {
        var seriesId = _db.SeedSeries(mappings: Mapping("fake"));
        var fake = new FakeSource { Name = "fake" };
        var listed = new List<SourceChapter> { fake.Chapter(1), fake.Chapter(2) };
        var source = new FakeSource { Name = "fake", OnListChapters = _ => listed };
        var cache = new SourceChapterListCache(TimeProvider.System, NullLogger<SourceChapterListCache>.Instance);
        ChapterSyncService Service() => new(
            _db.NewContext(), new SourceRegistry([source]),
            new DownloadQueueService(null!, TimeProvider.System, null!, NullLogger<DownloadQueueService>.Instance),
            Sources.AllEnabled, cache, new FakeAppSettings(), NullLogger<ChapterSyncService>.Instance);

        await Service().SyncSeriesAsync(seriesId);
        listed.Clear();
        await Service().SyncSeriesAsync(seriesId);

        Assert.Equal(2, LinksOf(seriesId).Count);
        using var db = _db.NewContext();
        Assert.NotNull(db.SourceMappings.Single(m => m.SeriesId == seriesId).LastError);
        var cached = await cache.GetAsync(source, "series", null);
        Assert.Equal(2, cached.Count);
    }

    [Fact]
    public async Task Empty_listing_is_accepted_for_a_mapping_with_no_links()
    {
        var seriesId = _db.SeedSeries(mappings: Mapping("fake"));
        var source = new FakeSource { Name = "fake", OnListChapters = _ => [] };

        await BuildService(null, source).SyncSeriesAsync(seriesId);

        using var db = _db.NewContext();
        var mapping = db.SourceMappings.Single(m => m.SeriesId == seriesId);
        Assert.Null(mapping.LastError);
        Assert.NotNull(mapping.ChapterSnapshotAt);
    }

    [Fact]
    public async Task MainOnly_mode_leaves_specials_unwanted()
    {
        var seriesId = _db.SeedSeries(monitor: NewChapterMonitorMode.MainOnly, mappings: Mapping("fake"));
        var fake = new FakeSource { Name = "fake" };
        var source = new FakeSource { Name = "fake", OnListChapters = _ => [fake.Chapter(10), fake.Chapter(10.5m)] };

        await BuildService(null, source).SyncSeriesAsync(seriesId);

        var chapters = ChaptersOf(seriesId);
        Assert.True(chapters.Single(c => c.Number == 10m).Wanted);
        Assert.False(chapters.Single(c => c.Number == 10.5m).Wanted);
    }

    /// <summary>
    /// A Smart series' new chapters are wanted like anyone else's — Smart decides *when* they get
    /// queued, not whether they exist. This used to stamp them unwanted (MonitoredUnder had no Smart
    /// case), which is why a Smart series' card read "10 / 10" however long the series really was.
    /// </summary>
    [Fact]
    public async Task Smart_mode_wants_new_chapters()
    {
        var seriesId = _db.SeedSeries(monitor: NewChapterMonitorMode.Smart, mappings: Mapping("fake"));
        var fake = new FakeSource { Name = "fake" };
        var source = new FakeSource { Name = "fake", OnListChapters = _ => [fake.Chapter(10), fake.Chapter(10.5m)] };

        await BuildService(null, source).SyncSeriesAsync(seriesId);

        Assert.All(ChaptersOf(seriesId), c => Assert.True(c.Wanted));
    }

    /// <summary>Smart can't be combined with MainOnly, so it reads the global specials setting.</summary>
    [Fact]
    public async Task Smart_mode_honours_the_specials_setting()
    {
        var seriesId = _db.SeedSeries(monitor: NewChapterMonitorMode.Smart, mappings: Mapping("fake"));
        var fake = new FakeSource { Name = "fake" };
        var source = new FakeSource { Name = "fake", OnListChapters = _ => [fake.Chapter(10), fake.Chapter(10.5m)] };
        var settings = new FakeAppSettings().Set(SettingKeys.MonitoringUnmonitorSpecials, "true");

        await BuildService(Sources.AllEnabled, null, settings, source).SyncSeriesAsync(seriesId);

        var chapters = ChaptersOf(seriesId);
        Assert.True(chapters.Single(c => c.Number == 10m).Wanted);
        Assert.False(chapters.Single(c => c.Number == 10.5m).Wanted);
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
    public async Task Globally_disabled_source_is_never_queried_and_keeps_its_mapping_enabled()
    {
        var seriesId = _db.SeedSeries(mappings: Mapping("fake"));
        var fake = new FakeSource { Name = "fake" };
        var source = new FakeSource { Name = "fake", OnListChapters = _ => [fake.Chapter(1)] };

        var newIds = await BuildService(Sources.Disabled("fake"), null, source).SyncSeriesAsync(seriesId);

        Assert.Empty(newIds);
        Assert.Equal(0, source.ListCalls);

        // The per-series flag is untouched, so switching the source back on restores it.
        using var db = _db.NewContext();
        Assert.True(db.SourceMappings.Single(m => m.SeriesId == seriesId).Enabled);
    }

    [Fact]
    public async Task Numbering_clash_clears_when_every_still_live_source_fetched()
    {
        // "All fetched" counts live mappings only: with one source switched off globally, the
        // remaining source fetching on its own is enough to clear a stale clash flag.
        var seriesId = _db.SeedSeries(mappings: [Mapping("fake"), Mapping("off")]);
        using (var db = _db.NewContext())
        {
            var series = db.Series.Single(s => s.Id == seriesId);
            series.NumberingClash = "off|fake";
            db.SaveChanges();
        }

        var fake = new FakeSource { Name = "fake" };
        var source = new FakeSource { Name = "fake", OnListChapters = _ => [fake.Chapter(1), fake.Chapter(2)] };

        await BuildService(Sources.Disabled("off"), null, source).SyncSeriesAsync(seriesId);

        using var check = _db.NewContext();
        Assert.Null(check.Series.Single(s => s.Id == seriesId).NumberingClash);
    }

    [Fact]
    public async Task Rate_limit_backs_off_the_queue_and_records_the_error()
    {
        var seriesId = _db.SeedSeries(mappings: Mapping("fake"));
        var queue = new DownloadQueueService(null!, TimeProvider.System, null!, NullLogger<DownloadQueueService>.Instance);
        var source = new FakeSource
        {
            Name = "fake",
            ListThrows = new RateLimitException("429", TimeSpan.FromMinutes(2))
        };

        var newIds = await BuildService(queue, source).SyncSeriesAsync(seriesId);

        Assert.Empty(newIds);
        Assert.True(queue.CooldownRemaining("fake") > TimeSpan.Zero);
        using var db = _db.NewContext();
        var mapping = db.SourceMappings.Single(m => m.SeriesId == seriesId);
        Assert.NotNull(mapping.LastError);
        Assert.Contains("paused", mapping.LastError);
    }

    [Fact]
    public async Task Ordinary_source_failure_is_recorded_without_touching_the_queue()
    {
        var seriesId = _db.SeedSeries(mappings: Mapping("fake"));
        var queue = new DownloadQueueService(null!, TimeProvider.System, null!, NullLogger<DownloadQueueService>.Instance);
        var source = new FakeSource { Name = "fake", ListThrows = new InvalidOperationException("boom") };

        var newIds = await BuildService(queue, source).SyncSeriesAsync(seriesId);

        Assert.Empty(newIds);
        Assert.Equal(TimeSpan.Zero, queue.CooldownRemaining("fake"));
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
