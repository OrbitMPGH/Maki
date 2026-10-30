using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Maki.Api.Configuration;
using Maki.Api.Controllers;
using Maki.Api.Dtos;
using Maki.Api.Jobs;
using Maki.Api.Komga;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Kavita;
using Maki.Core.Opds;
using Maki.Core.Quality;
using Maki.Core.Recommendations;
using Maki.Core.Scrobbling;
using Maki.Core.Sources;
using Maki.Data;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// An archived series stops talking to sources and the download queue. Each fact seeds one active and
/// one archived series of the same shape and checks that the guarded member leaves only the active one
/// to act on, so reverting a single guard fails exactly one fact. Reads of the database alone are not
/// covered here on purpose: they keep showing an archived series.
/// </summary>
public sealed class ArchiveGuardTests : IDisposable
{
    private static readonly DateTime ArchivedAt = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string ArchivedKey = "error.series.archived";

    private readonly TestDb _db = new();
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "maki-archive-guard-" + Guid.NewGuid().ToString("N")[..8]);

    public ArchiveGuardTests()
    {
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_tmp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static SourceMapping Mapping(string? lastError = null) => new()
    {
        SourceName = "fake", SourceSeriesId = "s", Url = "https://fake.test", Priority = 1, Enabled = true,
        LastError = lastError,
    };

    private (int Active, int Archived) Pair(
        NewChapterMonitorMode monitor = NewChapterMonitorMode.All, Action<Series>? configure = null,
        bool mapped = true, string? mappingError = null)
    {
        SourceMapping[] Mappings() => mapped ? [Mapping(mappingError)] : [];
        var active = _db.SeedSeries("Active", monitor, configure: configure, mappings: Mappings());
        var archived = _db.SeedSeries("Archived", monitor, configure: s =>
        {
            configure?.Invoke(s);
            s.ArchivedAt = ArchivedAt;
        }, mappings: Mappings());
        return (active, archived);
    }

    private int SeedChapter(int seriesId, decimal number)
    {
        using var db = _db.NewContext();
        var chapter = new Chapter { SeriesId = seriesId, Number = number, Language = "en" };
        db.Chapters.Add(chapter);
        db.SaveChanges();
        return chapter.Id;
    }

    /// <summary>A downloaded chapter with a real archive under the temp root, in a folder named for the series.</summary>
    private (int ChapterId, int FileId, string Path) SeedDownloaded(int seriesId, decimal number)
    {
        using var db = _db.NewContext();
        var series = db.Series.Single(s => s.Id == seriesId);
        db.RootFolders.Single(r => r.Id == series.RootFolderId).Path = _tmp;
        db.SaveChanges();

        var relative = System.IO.Path.Combine(series.FolderName, $"ch{number}.cbz");
        var path = System.IO.Path.Combine(_tmp, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var page = zip.CreateEntry("001.jpg").Open();
            page.WriteByte(0xFF);
        }

        var file = new ChapterFile
        {
            SeriesId = seriesId, RelativePath = relative, Size = new FileInfo(path).Length, SourceName = "Test",
            DateAdded = DateTime.UtcNow,
        };
        db.ChapterFiles.Add(file);
        db.SaveChanges();
        var chapter = new Chapter { SeriesId = seriesId, Number = number, Language = "en", ChapterFileId = file.Id };
        db.Chapters.Add(chapter);
        db.SaveChanges();
        return (chapter.Id, file.Id, path);
    }

    private static string Code(IActionResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<ConflictObjectResult>(result).Value).GetProperty("code").GetString()!;

    private static void AssertArchived(IActionResult result) => Assert.Equal(ArchivedKey, Code(result));

    // ---- list selectors ----

    [Fact]
    public async Task Refreshable_skips_archived_series()
    {
        var (active, _) = Pair();

        using var db = _db.NewContext();
        Assert.Equal([active], await RefreshMonitoredSeriesJob.RefreshableSeriesIdsAsync(db, []));
    }

    [Fact]
    public async Task SeriesNeedingTopUp_skips_archived_series()
    {
        var (active, _) = Pair(NewChapterMonitorMode.Smart);

        using var db = _db.NewContext();
        var due = await SmartDownloadJob.SeriesNeedingTopUpAsync(db, 5, CancellationToken.None);
        Assert.Equal([active], due.Select(t => t.Series.Id));
    }

    [Fact]
    public async Task CandidateSeries_skips_archived_series()
    {
        var (active, _) = Pair();

        using var db = _db.NewContext();
        var service = new TorrentUpgradeService(db, null!, null!, null!, null!, TimeProvider.System, null!,
            NullLogger<TorrentUpgradeService>.Instance);
        Assert.Equal([active], await service.CandidateSeriesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadFileCleanup_skips_archived_series()
    {
        var (active, archived) = Pair(configure: s => s.ReadFileCleanup = ReadFileCleanup.On);
        var paths = new List<string>();
        var long_ago = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var id in new[] { active, archived })
        {
            var first = SeedDownloaded(id, 1m);
            var second = SeedDownloaded(id, 2m);
            paths.Add(first.Path);
            using var db = _db.NewContext();

            // Keep-last holds back a reader's most recent finish, so chapter 1 is the one that is due.
            foreach (var (chapter, finished) in new[] { (first.ChapterId, long_ago.AddDays(-30)), (second.ChapterId, long_ago) })
            {
                db.ChapterProgress.Add(new ChapterProgress
                {
                    UserId = 1, SeriesId = id, ChapterId = chapter, Completed = true, PageCount = 0,
                    StartedAt = finished, UpdatedAt = finished, CompletedAt = finished,
                });
            }

            db.SaveChanges();
        }

        var clock = new StoppedClock(DateTimeOffset.UtcNow.AddDays(60));
        using var context = _db.NewContext();
        var service = new ReadFileCleanupService(
            context, new SettingsService(_db.ScopeFactory()),
            new ChapterFileDeletion(context, new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance), clock,
                TestRecycleBin.For(context), NullLogger<ChapterFileDeletion>.Instance),
            clock, NullLogger<ReadFileCleanupService>.Instance);

        await service.RunAsync(CancellationToken.None);

        Assert.False(File.Exists(paths[0]));
        Assert.True(File.Exists(paths[1]));
    }

    [Fact]
    public async Task HealthCheck_failing_mappings_skip_archived_series()
    {
        var (active, _) = Pair(mappingError: "boom");

        var issues = await Health().GetIssuesAsync();

        Assert.Equal([active], issues.Where(i => i.Type == "sourceMapping").Select(i => i.SeriesId!.Value));
    }

    [Fact]
    public async Task HealthCheck_no_source_mappings_skips_archived_series()
    {
        var (active, _) = Pair(mapped: false);

        var issues = await Health().GetIssuesAsync();

        Assert.Equal([active], issues.Where(i => i.MessageKey == "health.issue.noSourceMappings").Select(i => i.SeriesId!.Value));
    }

    private HealthCheckService Health()
    {
        var registry = new SourceRegistry([new FakeSource { Name = "fake" }]);
        var dump = new MangaBakaDumpService(null!,
            new MangaBakaDumpOptions(Path.Combine(_tmp, "mangabaka.db"), _tmp), new FakeAppSettings(),
            new MangaBakaDumpStatus(), NullLogger<MangaBakaDumpService>.Instance);
        return new HealthCheckService(_db.NewContext(), new SourceAvailability(new FakeAppSettings(), registry), registry, dump);
    }

    [Fact]
    public async Task HealthSourceRecovery_Failing_skips_archived_series()
    {
        var (active, _) = Pair(mappingError: "boom");

        using var db = _db.NewContext();
        var failing = await HealthSourceRecovery.Failing(db, "fake").Select(m => m.SeriesId).ToListAsync();
        Assert.Equal([active], failing);
    }

    [Fact]
    public async Task SourceMatchWorker_startup_requeue_skips_archived_series()
    {
        var (active, _) = Pair(configure: s => s.SourceMatchPending = true);
        var queue = new SourceMatchQueue();
        var worker = new SourceMatchWorkerHostedService(queue, _db.ScopeFactory(), NullLogger<SourceMatchWorkerHostedService>.Instance);

        var recover = typeof(SourceMatchWorkerHostedService).GetMethod("RecoverAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)recover.Invoke(worker, [CancellationToken.None])!;

        var queued = new List<int>();
        while (queue.TryReadMatch(out var id))
        {
            queued.Add(id);
        }

        Assert.Equal([active], queued);
    }

    [Fact]
    public async Task NativePass_plan_to_read_skips_archived_series()
    {
        var (active, archived) = Pair();
        using (var db = _db.NewContext())
        {
            db.Series.Single(s => s.Id == active).MangaBakaId = 100;
            db.Series.Single(s => s.Id == archived).MangaBakaId = 200;
            db.SaveChanges();
        }

        var tracker = new PushRecorder();

        await Scrobbler().NativePassAsync(1, [tracker], ownsKavita: false, CancellationToken.None);

        Assert.Contains(("100", ScrobbleStatus.PlanToRead), tracker.Pushes);
        Assert.DoesNotContain("200", tracker.RemoteIds);
    }

    [Fact]
    public async Task NativePass_still_pushes_the_completion_of_an_archived_finished_one_shot()
    {
        var (_, archived) = Pair();
        using (var db = _db.NewContext())
        {
            db.Series.Single(s => s.Id == archived).MangaBakaId = 200;
            db.SaveChanges();
        }

        var tracker = new PushRecorder();

        await Scrobbler().NativePassAsync(1, [tracker], ownsKavita: false, CancellationToken.None,
            oneShots: new Dictionary<int, bool> { [archived] = true });

        Assert.Equal([("200", ScrobbleStatus.Completed)], tracker.Pushes);
    }

    private ScrobbleService Scrobbler() => new(
        _db.ScopeFactory(), settings: null!, new PlanToReadSettings(), kavitaUser: null!, kavita: null!,
        volumeBoundaries: null!, anilist: null!, mal: null!, mangaBaka: null!, kitsu: null!, mangaUpdates: null!,
        logger: NullLogger<ScrobbleService>.Instance);

    [Fact]
    public async Task Home_reading_rail_skips_archived_series()
    {
        var (active, archived) = Pair();
        foreach (var id in new[] { active, archived })
        {
            var (chapterId, _, _) = SeedDownloaded(id, 1m);
            SeedDownloaded(id, 2m);
            using var db = _db.NewContext();
            db.ChapterProgress.Add(new ChapterProgress
            {
                UserId = 1, SeriesId = id, ChapterId = chapterId, PageIndex = 5, PageCount = 20,
                StartedAt = ArchivedAt, UpdatedAt = ArchivedAt,
            });
            db.SaveChanges();
        }

        var response = Assert.IsType<HomeReadingResponse>(Assert.IsType<OkObjectResult>(
            await Home().Reading(ct: CancellationToken.None)).Value);

        Assert.Equal([active], response.ContinueReading.Select(i => i.SeriesId));
    }

    [Fact]
    public async Task Home_recently_added_rail_skips_archived_series()
    {
        var (active, archived) = Pair();
        SeedDownloaded(active, 1m);
        SeedDownloaded(archived, 1m);

        // The archived file is the newer one, so a filter after the take would leave the rail empty.
        var items = Assert.IsType<List<HomeRecentSeriesItem>>(Assert.IsType<OkObjectResult>(
            await Home().RecentlyAdded(limit: 1, ct: CancellationToken.None)).Value);

        Assert.Equal([active], items.Select(i => i.SeriesId));
    }

    private HomeController Home()
    {
        var context = _db.NewContext();
        var paths = new AppPaths(Path.Combine(_tmp, "config"));
        var covers = new CoverService(null!, paths, new FakeAppSettings(), NullLogger<CoverService>.Instance);
        return new HomeController(context, new ContinueReadingService(context),
            new CoverAccentCache(covers, NullLogger<CoverAccentCache>.Instance));
    }

    [Fact]
    public async Task RecentActivityRail_seeds_skip_archived_series()
    {
        var (active, archived) = Pair();
        foreach (var (id, mangaBakaId) in new[] { (active, 101), (archived, 202) })
        {
            using var db = _db.NewContext();
            db.Series.Single(s => s.Id == id).MangaBakaId = mangaBakaId;
            var file = new ChapterFile { SeriesId = id, RelativePath = $"{id}.cbz", DateAdded = ArchivedAt };
            db.ChapterFiles.Add(file);
            db.SaveChanges();
            var chapter = new Chapter { SeriesId = id, Number = 1, ChapterFileId = file.Id };
            db.Chapters.Add(chapter);
            db.SaveChanges();
            db.ChapterProgress.Add(new ChapterProgress
            {
                UserId = 1, SeriesId = id, ChapterId = chapter.Id, PageCount = 20, Completed = true, ReadSeconds = 600,
                StartedAt = ArchivedAt, UpdatedAt = ArchivedAt,
            });
            db.SaveChanges();
        }

        var recommender = new SeedRecorder();
        var settings = new FakeAppSettings();
        var weights = new SeedWeightService(new BehavioralTasteService(TasteTuning.Default), TasteTuning.Default, settings);
        var recommendations = new RecommendationService(_db.ScopeFactory(), new EmptyStore(), recommender, weights, settings,
            NullLogger<RecommendationService>.Instance);
        var rail = new RecentActivityRailService(_db.ScopeFactory(), recommendations, weights,
            NullLogger<RecentActivityRailService>.Instance);

        await rail.GetAsync(new TestCurrentUser(1), refresh: false);

        Assert.Equal([101L], recommender.Seen.Single());
    }

    [Fact]
    public async Task SideInterestRail_seeds_skip_archived_series()
    {
        var (active, archived) = Pair();
        using (var db = _db.NewContext())
        {
            db.Series.Single(s => s.Id == active).MangaBakaId = 1;
            db.Series.Single(s => s.Id == archived).MangaBakaId = 2;
            db.SaveChanges();
        }

        var service = new SideInterestRailService(_db.ScopeFactory(), null!,
            new SeedWeightService(new BehavioralTasteService(TasteTuning.Default), TasteTuning.Default, new FakeAppSettings()),
            null!, null!);
        var seeds = await service.ReadSeedsAsync(new TestCurrentUser(1), default);

        Assert.Equal(1, Assert.Single(seeds).Id);
    }

    [Fact]
    public async Task CustomRail_library_rows_skip_archived_series()
    {
        Pair();
        var service = new CustomRailService(_db.NewContext(1), new TestCurrentUser(1), null!, null!, null!, null!, null!);

        var count = await service.CountAsync(new CustomRailSpec(Source: CustomRailSources.Library), CancellationToken.None);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task AnimeResume_library_rail_skips_archived_series()
    {
        var (active, archived) = Pair();
        AnimeResumeFixture.OptIn(_db, 1);
        using (var db = _db.NewContext())
        {
            db.Series.Single(s => s.Id == active).MangaBakaId = 77;
            db.Series.Single(s => s.Id == archived).MangaBakaId = 78;
            foreach (var id in new[] { active, archived })
            {
                var series = db.Series.Single(s => s.Id == id);
                series.HasAnime = true;
                series.AnimeStart = "Chap 1 (S1)";
                series.AnimeEnd = AnimeResumeFixture.CoversToFive;
            }

            db.SaveChanges();
        }

        AnimeResumeFixture.SeedSignal(_db, 1, animeId: 1, mangaBakaId: 77);
        AnimeResumeFixture.SeedSignal(_db, 1, animeId: 2, mangaBakaId: 78);
        AnimeResumeFixture.SeedChapters(_db, active, 1, 2, 3, 4, 5, 6, 7, 8);
        AnimeResumeFixture.SeedChapters(_db, archived, 1, 2, 3, 4, 5, 6, 7, 8);

        var page = await AnimeResumeFixture.Service(_db, _db.NewContext(1), new ReadingProgressGate())
            .RailAsync(CancellationToken.None);

        Assert.Equal([active], page.Items.Select(i => i.SeriesId!.Value));
    }

    [Fact]
    public async Task OpdsSeriesFeed_skips_archived_series()
    {
        var (active, archived) = Pair();
        SeedDownloaded(active, 1m);
        SeedDownloaded(archived, 1m);

        var feed = await Opds().SeriesFeedAsync(new OpdsContext(string.Empty, "tok3n"), 0, CancellationToken.None);

        Assert.Equal(["Active"], feed.Entries.Select(e => e.Title));
    }

    [Fact]
    public async Task OpdsRecentShelf_skips_archived_series()
    {
        var (active, archived) = Pair();
        SeedDownloaded(active, 1m);
        SeedDownloaded(archived, 1m);
        SeedFileRows(archived, OpdsCatalogService.ShelfSize, DateTime.UtcNow.AddHours(1));

        var feed = await Opds().RecentFeedAsync(new OpdsContext(string.Empty, "tok3n"), CancellationToken.None);

        var entry = Assert.Single(feed.Entries);
        Assert.Contains("Active", entry.Title);
    }

    [Fact]
    public async Task OpdsOnDeckShelf_skips_archived_series()
    {
        var (active, _) = Pair();
        var (activeChapter, _, _) = SeedDownloaded(active, 1m);
        SeedDownloaded(active, 2m);
        Progress(active, activeChapter, ArchivedAt);
        for (var i = 0; i < OpdsCatalogService.ShelfSize; i++)
        {
            var id = _db.SeedSeries($"Archived {i}", configure: s => s.ArchivedAt = ArchivedAt);
            var (chapterId, _, _) = SeedDownloaded(id, 1m);
            Progress(id, chapterId, ArchivedAt.AddDays(1));
        }

        var feed = await Opds().OnDeckFeedAsync(new OpdsContext(string.Empty, "tok3n"), CancellationToken.None);

        var entry = Assert.Single(feed.Entries);
        Assert.Contains("Active", entry.Title);
    }

    private void Progress(int seriesId, int chapterId, DateTime at)
    {
        using var db = _db.NewContext();
        db.ChapterProgress.Add(new ChapterProgress
        {
            UserId = 1, SeriesId = seriesId, ChapterId = chapterId, PageIndex = 20, PageCount = 20, Completed = true,
            StartedAt = at, UpdatedAt = at,
        });
        db.SaveChanges();
    }

    /// <summary>Chapter and file rows only, with nothing on disk: enough to fill a shelf before its filter.</summary>
    private void SeedFileRows(int seriesId, int count, DateTime added)
    {
        using var db = _db.NewContext();
        for (var i = 0; i < count; i++)
        {
            var file = new ChapterFile
            {
                SeriesId = seriesId, RelativePath = $"rows/{i}.cbz", Size = 1, SourceName = "Test", DateAdded = added,
            };
            db.ChapterFiles.Add(file);
            db.SaveChanges();
            db.Chapters.Add(new Chapter { SeriesId = seriesId, Number = 100 + i, Language = "en", ChapterFileId = file.Id });
        }

        db.SaveChanges();
    }

    private OpdsCatalogService Opds()
    {
        var context = _db.NewContext();
        return new OpdsCatalogService(context, Reader(context), new ContinueReadingService(context), new TestLocalizer());
    }

    private ReaderService Reader(MakiDbContext context) => new(
        context, new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance),
        new ReadingProgressService(context, new ReadingProgressGate(), NullLogger<ReadingProgressService>.Instance),
        InertKavitaPusher.For(_db.ScopeFactory()), new ReadingSessionService(context), NullLogger<ReaderService>.Instance);

    private KomgaCatalogService Komga()
    {
        var context = _db.NewContext(1);
        return new KomgaCatalogService(context, Reader(context), new ContinueReadingService(context));
    }

    [Fact]
    public async Task KomgaLibraries_skip_archived_series()
    {
        var (active, archived) = Pair();
        SeedDownloaded(active, 1m);
        SeedDownloaded(archived, 1m);
        int activeRoot;
        using (var db = _db.NewContext())
        {
            activeRoot = db.Series.Single(s => s.Id == active).RootFolderId;
            Assert.NotEqual(activeRoot, db.Series.Single(s => s.Id == archived).RootFolderId);
        }

        var libraries = await Komga().LibrariesAsync(CancellationToken.None);

        Assert.Equal([activeRoot.ToString()], libraries.Select(l => l.Id));
    }

    [Fact]
    public async Task KomgaSeries_skip_archived_series()
    {
        var (active, archived) = Pair();
        SeedDownloaded(active, 1m);
        SeedDownloaded(archived, 1m);

        var page = await Komga().SeriesAsync(new KomgaSeriesQuery(), 0, null, CancellationToken.None);

        Assert.Equal([active.ToString()], page.Content.Select(s => s.Id));
    }

    [Fact]
    public async Task KomgaBooks_skip_archived_series()
    {
        var (active, archived) = Pair();
        var kept = SeedDownloaded(active, 1m).ChapterId;
        SeedDownloaded(archived, 1m);

        var page = await Komga().BooksAsync(new KomgaBookQuery(), 0, null, CancellationToken.None);

        Assert.Equal([kept.ToString()], page.Content.Select(b => b.Id));
    }

    // ---- per-series entry points ----

    private SeriesController SeriesController(
        MakiDbContext db, ILogger<SeriesController>? logger = null, CbzLinkService? link = null) => new(
        localizer: new TestLocalizer(),
        db: db,
        coverService: new CoverService(null!, new AppPaths(_tmp), new FakeAppSettings(), NullLogger<CoverService>.Instance),
        chapterSyncService: null!,
        cbzLinkService: link!,
        relinkPlanner: null!,
        seriesCreation: null!,
        seriesRename: null!,
        metadataRefresh: null!,
        downloadQueue: null!,
        downloadBatches: null!,
        appSettings: null!,
        kavitaScans: null!,
        scrobbler: null!,
        stats: new StatsEventService(db),
        mangaBakaStore: null!,
        similarSeries: null!,
        recommendationFeedback: null!,
        readingProfiles: null!,
        readingTimeEstimates: null!,
        sourceAvailability: null!,
        currentUser: new TestCurrentUser(1),
        userSettings: null!,
        notifications: new RecordingNotifications(),
        locales: new TestUserLocaleResolver(),
        logger: logger ?? NullLogger<SeriesController>.Instance);

    [Fact]
    public async Task SeriesRefresh_refuses_archived_series()
    {
        var (_, archived) = Pair();

        AssertArchived(await SeriesController(_db.NewContext()).Refresh(archived, default));
    }

    [Fact]
    public async Task SeriesSearchMissing_refuses_archived_series()
    {
        var (_, archived) = Pair();
        SeedChapter(archived, 1m);

        AssertArchived(await SeriesController(_db.NewContext()).SearchMissing(archived, default));
    }

    [Fact]
    public async Task SeriesDownloadNext_refuses_archived_series()
    {
        var (_, archived) = Pair();
        SeedChapter(archived, 1m);

        AssertArchived(await SeriesController(_db.NewContext()).DownloadNext(archived, new(5), default));
    }

    [Fact]
    public async Task SeriesRescan_on_an_archived_series_relinks_files_without_syncing_the_source()
    {
        var (_, archived) = Pair();
        var logger = new CapturingLogger<SeriesController>();
        using var db = _db.NewContext();
        db.RootFolders.Single(r => r.Id == db.Series.Single(s => s.Id == archived).RootFolderId).Path = _tmp;
        db.SaveChanges();
        Directory.CreateDirectory(Path.Combine(_tmp, "Archived"));

        var result = await SeriesController(db, logger, LinkService(db)).Rescan(archived, default);

        Assert.IsType<OkObjectResult>(result);
        // chapterSyncService is null in this controller, so a sync attempt would be caught and logged.
        Assert.DoesNotContain(logger.Warnings, w => w.Contains("Chapter sync failed"));
    }

    private CbzLinkService LinkService(MakiDbContext db)
    {
        var settings = new FakeAppSettings();
        var registry = new SourceRegistry([]);
        return new CbzLinkService(
            db, registry,
            new KavitaScanService(new KavitaClient(new StubHttpClientFactory("{}")), settings, _db.ScopeFactory(),
                NullLogger<KavitaScanService>.Instance),
            new StatsEventService(db), new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance),
            new SourceAvailability(settings, registry), TestQuality.Create(registry), settings,
            NullLogger<CbzLinkService>.Instance);
    }

    private static UpgradesController Upgrades(MakiDbContext db) =>
        new(null!, db, new TestLocalizer(), NullLogger<UpgradesController>.Instance);

    [Fact]
    public async Task UpgradesScan_refuses_an_archived_series()
    {
        var (_, archived) = Pair();

        var result = await Upgrades(_db.NewContext()).Scan(
            new UpgradeScanRequest(archived), null!, null!, new TestCurrentUser(1), new UpgradeScanTracker(), default);

        AssertArchived(result);
    }

    [Fact]
    public async Task UpgradesScan_refuses_a_chapter_of_an_archived_series()
    {
        var (_, archived) = Pair();
        var (chapterId, _, _) = SeedDownloaded(archived, 1m);

        var result = await Upgrades(_db.NewContext()).Scan(
            new UpgradeScanRequest(null, chapterId), null!, null!, new TestCurrentUser(1), new UpgradeScanTracker(), default);

        AssertArchived(result);
    }

    [Fact]
    public async Task UpgradesVolumeSearch_refuses_an_archived_series()
    {
        var (_, archived) = Pair();

        var result = await Upgrades(_db.NewContext()).VolumeSearch(
            new VolumeSearchRequest(archived), null!, null!, new TestCurrentUser(1), default);

        AssertArchived(result);
    }

    private SourceMappingController Mappings(MakiDbContext db, SourceMatchQueue? queue = null) => new(
        new TestLocalizer(), db, new SourceRegistry([new FakeSource { Name = "fake" }]), new FakeAppSettings(),
        Sources.AllEnabled, queue ?? new SourceMatchQueue(), null!, null!, null!, null!, null!, new TestCurrentUser(1),
        NullLogger<SourceMappingController>.Instance);

    [Fact]
    public async Task SourceMappingAutoMatch_queues_only_active_series()
    {
        var (active, archived) = Pair();
        var queue = new SourceMatchQueue();

        var result = await Mappings(_db.NewContext(), queue).AutoMatch(new([active, archived]), default);

        Assert.IsType<OkObjectResult>(result);
        var queued = new List<int>();
        while (queue.TryReadMatch(out var id))
        {
            queued.Add(id);
        }

        Assert.Equal([active], queued);
    }

    [Fact]
    public async Task SourceMappingAutoMatch_refuses_when_every_series_is_archived()
    {
        var (_, archived) = Pair();

        AssertArchived(await Mappings(_db.NewContext()).AutoMatch(new([archived]), default));
    }

    [Fact]
    public async Task SourceMappingRefreshSnapshots_refuses_archived_series()
    {
        var (_, archived) = Pair();

        AssertArchived(await Mappings(_db.NewContext()).RefreshSnapshots(new(archived, 0), default));
    }

    [Fact]
    public async Task SourceMappingCompare_refuses_archived_series()
    {
        var (_, archived) = Pair();

        AssertArchived(await Mappings(_db.NewContext()).StartCompare(new(archived), default));
    }

    [Fact]
    public async Task SourceMappingQuality_refuses_archived_series()
    {
        var (_, archived) = Pair();

        AssertArchived(await Mappings(_db.NewContext()).Quality(archived, null!, default));
    }

    [Fact]
    public async Task SourceMappingScout_refuses_archived_series()
    {
        var (_, archived) = Pair();

        AssertArchived(await Mappings(_db.NewContext()).Scout(new(archived), default));
    }

    [Fact]
    public async Task SourceScout_StartIfEnabled_returns_early_for_an_archived_series()
    {
        var (active, archived) = Pair();
        _db.SetConfig((SettingKeys.SourcesScoutOnMatch, "true"));
        var scout = new SourceScoutService(_db.ScopeFactory(), new SourceRegistry([]), Sources.AllEnabled, null!, null!,
            NullLogger<SourceScoutService>.Instance);

        using var db = _db.NewContext();
        await scout.StartIfEnabledAsync(db, archived, CancellationToken.None);
        await scout.StartIfEnabledAsync(db, active, CancellationToken.None);

        Assert.Null(scout.Snapshot(archived));
        Assert.NotNull(scout.Snapshot(active));
    }

    // ---- chapter endpoints ----

    private ChapterController Chapters(MakiDbContext db, params FakeSource[] sources)
    {
        var registry = new SourceRegistry(sources);
        return new ChapterController(
            new TestLocalizer(), db, Queue(), new StatsEventService(db),
            new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance), registry,
            new SourceAvailability(new FakeAppSettings(), registry),
            new SourceChapterListCache(TimeProvider.System, NullLogger<SourceChapterListCache>.Instance),
            new DownloadBatchNotifier(new RecordingNotifications(), new RecordingInbox(), new TestLocalizer(),
                new TestUserLocaleResolver(), TimeProvider.System, NullLogger<DownloadBatchNotifier>.Instance),
            new TestCurrentUser(1), NullLogger<ChapterController>.Instance);
    }

    [Fact]
    public async Task ChapterSearch_refuses_a_chapter_of_an_archived_series()
    {
        var (_, archived) = Pair();
        var chapterId = SeedChapter(archived, 1m);

        using var db = _db.NewContext();
        AssertArchived(await Chapters(db).Search(chapterId, default));
        Assert.Equal(0, QueueRows(archived));
    }

    [Fact]
    public async Task ChapterDownloadFrom_refuses_a_chapter_of_an_archived_series()
    {
        var (_, archived) = Pair();
        var chapterId = SeedChapter(archived, 1m);

        using var db = _db.NewContext();
        var mappingId = db.SourceMappings.Single(m => m.SeriesId == archived).Id;
        AssertArchived(await Chapters(db).DownloadFrom(chapterId, new DownloadChapterFromRequest(mappingId), null!, default));
        Assert.Equal(0, QueueRows(archived));
    }

    [Fact]
    public async Task ChapterRedownload_refuses_an_archived_series_before_listing_the_source()
    {
        var (_, archived) = Pair();
        var (_, fileId, _) = SeedDownloaded(archived, 1m);
        using (var db = _db.NewContext())
        {
            db.ChapterFiles.Single(f => f.Id == fileId).SourceName = "other";
            db.SaveChanges();
        }

        var listed = 0;
        var fake = new FakeSource
        {
            Name = "fake",
            OnListChapters = _ =>
            {
                listed++;
                return [];
            },
        };

        using var context = _db.NewContext();
        AssertArchived(await Chapters(context, fake, new FakeSource { Name = "other" })
            .Redownload(new RedownloadRequest(archived, "fake"), null!, default));
        Assert.Equal(0, listed);
    }

    [Fact]
    public async Task ChapterDownloadBulk_refuses_only_a_selection_made_of_archived_series()
    {
        var (active, archived) = Pair();
        var activeChapter = SeedChapter(active, 1m);
        var archivedChapter = SeedChapter(archived, 1m);

        using var db = _db.NewContext();
        AssertArchived(await Chapters(db).DownloadBulk(new DownloadChaptersRequest([archivedChapter]), default));

        var mixed = Assert.IsType<OkObjectResult>(
            await Chapters(db).DownloadBulk(new DownloadChaptersRequest([archivedChapter, activeChapter]), default));
        Assert.Equal(1, JsonSerializer.SerializeToElement(mixed.Value).GetProperty("queued").GetInt32());
        Assert.Equal(0, QueueRows(archived));
    }

    // ---- torrent releases ----

    private readonly FakeTorrentClient _torrentClient = new();

    private ReleaseService Releases(MakiDbContext db) => new(
        db, TestIndexers.Registry(new StubHttpClientFactory("[]")), _torrentClient.Registry(),
        new SettingsService(_db.ScopeFactory()), NullLogger<ReleaseService>.Instance);

    private ReleaseController ReleaseApi(MakiDbContext db) => new(
        Releases(db), new ReleaseSearchCache(new MemoryCache(new MemoryCacheOptions())), new TestLocalizer(),
        NullLogger<ReleaseController>.Instance);

    [Fact]
    public async Task ReleaseSearch_refuses_an_archived_series()
    {
        var (_, archived) = Pair();

        using var db = _db.NewContext();
        AssertArchived(await ReleaseApi(db).Search(archived, null, torrents: null!, CancellationToken.None));
    }

    [Fact]
    public async Task ReleaseGrab_refuses_an_archived_series()
    {
        var (_, archived) = Pair();

        using var db = _db.NewContext();
        AssertArchived(await ReleaseApi(db).Grab(
            new ReleaseController.GrabRequest(archived, new ReleaseController.GrabRelease("g")), new TestCurrentUser(1),
            db, CancellationToken.None));
    }

    [Fact]
    public async Task ReleaseService_GrabAsync_refuses_an_archived_series()
    {
        var (active, archived) = Pair();
        await new SettingsService(_db.ScopeFactory()).SetAsync(SettingKeys.DownloadClientUrl, "http://client.test");
        const string magnet = "magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567";
        var release = new ReleaseDto("g", "Series v01", 1000, "Nyaa", 10, 1, "torrent", null, magnet, null);

        using var db = _db.NewContext();
        Assert.NotNull(await Releases(db).GrabAsync(active, release, DownloadOrigin.Manual, 1, null));
        var refused = await Assert.ThrowsAsync<ReleaseRefusedException>(() =>
            Releases(db).GrabAsync(archived, release, DownloadOrigin.Upgrade, null, null));

        Assert.Equal(ArchivedKey, refused.Key);
        Assert.Single(_torrentClient.AddedLinks);
        Assert.Equal(0, QueueRows(archived));
    }

    // ---- queue backstop ----

    private DownloadQueueService Queue() => new(
        _db.ScopeFactory(), TimeProvider.System, Sources.SingleChapterResolver(null, "fake"),
        NullLogger<DownloadQueueService>.Instance);

    private int QueueRows(int seriesId)
    {
        using var db = _db.NewContext();
        return db.DownloadQueue.Count(q => q.SeriesId == seriesId);
    }

    [Fact]
    public async Task EnqueueChapterAsync_refuses_archived_series()
    {
        var (active, archived) = Pair();
        var activeChapter = SeedChapter(active, 1m);
        var archivedChapter = SeedChapter(archived, 1m);
        var queue = Queue();

        Assert.NotNull(await queue.EnqueueChapterAsync(activeChapter, origin: DownloadOrigin.Manual));
        Assert.Null(await queue.EnqueueChapterAsync(archivedChapter, origin: DownloadOrigin.Manual));
        Assert.Equal(0, QueueRows(archived));
    }

    [Fact]
    public async Task EnqueueChaptersAsync_leaves_archived_chapters_out()
    {
        var (active, archived) = Pair();
        var activeChapter = SeedChapter(active, 1m);
        var archivedChapter = SeedChapter(archived, 1m);

        var result = await Queue().EnqueueChaptersAsync([archivedChapter, activeChapter], DownloadOrigin.Manual, 1);

        Assert.Equal(activeChapter, Assert.Single(result.Queued).ChapterId);
        Assert.Equal(0, QueueRows(archived));
    }

    [Fact]
    public async Task EnqueueUpgradeAsync_refuses_archived_series()
    {
        var (active, archived) = Pair();
        var queue = Queue();
        SeedDownloaded(active, 1m);
        SeedDownloaded(archived, 1m);

        async Task<DownloadQueueItem?> Upgrade(int seriesId)
        {
            using var db = _db.NewContext();
            var chapter = db.Chapters.AsNoTracking().Single(c => c.SeriesId == seriesId);
            var mappingId = db.SourceMappings.Single(m => m.SeriesId == seriesId).Id;
            var info = new UpgradeInfo { ChapterFileId = chapter.ChapterFileId!.Value };
            return await queue.EnqueueUpgradeAsync(chapter.Id, mappingId, "s1", info, null, CancellationToken.None);
        }

        Assert.NotNull(await Upgrade(active));
        Assert.Null(await Upgrade(archived));
        Assert.Equal(0, QueueRows(archived));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Requeueing_failed_rows_skips_archived_series(bool manual)
    {
        var (active, archived) = Pair();
        foreach (var id in new[] { active, archived })
        {
            var chapterId = SeedChapter(id, 1m);
            using var db = _db.NewContext();
            db.DownloadQueue.Add(new DownloadQueueItem
            {
                SeriesId = id, ChapterId = chapterId, Protocol = AcquisitionProtocol.Scraper, Status = QueueStatus.Failed,
                QueuedAt = DateTime.UtcNow, RetryCount = 1,
            });
            db.SaveChanges();
        }

        var queue = Queue();
        var requeued = manual
            ? await queue.RetryFailedAsync(null, null, new DataScope())
            : await queue.RequeueEligibleFailuresAsync(5);

        Assert.Equal(1, requeued);
        using var check = _db.NewContext();
        Assert.Equal(QueueStatus.Queued, check.DownloadQueue.Single(q => q.SeriesId == active).Status);
        Assert.Equal(QueueStatus.Failed, check.DownloadQueue.Single(q => q.SeriesId == archived).Status);
    }

    [Fact]
    public async Task EnqueueRepairAsync_stays_open_for_an_archived_series()
    {
        var (_, archived) = Pair();
        var (chapterId, _, _) = SeedDownloaded(archived, 1m);

        using var db = _db.NewContext();
        var chapter = db.Chapters.Single(c => c.Id == chapterId);
        var mapping = db.SourceMappings.Single(m => m.SeriesId == archived);
        await Queue().EnqueueRepairAsync(
            db, 1, chapter, new ResolvedChapterSource(mapping, new FakeSource { Name = "fake" }, "s1"), 1, CancellationToken.None);

        Assert.Equal(1, QueueRows(archived));
    }

    // ---- the list of guarded members ----

    private static readonly string[] GuardedQueueEntryPoints =
    [
        "EnqueueChapterAsync", "EnqueueChaptersAsync", "EnqueueUpgradeAsync", "RequeueEligibleFailuresAsync",
        "RetryFailedAsync",
        // Open on purpose: a health repair for an archived series still has to be able to run.
        "EnqueueRepairAsync",
    ];

    private static readonly string[] SeriesIdJobMembers =
    [
        // Guarded through their callers: the refresh action, restore, and the health recovery selector.
        "RefreshMonitoredSeriesJob.RefreshSeriesAsync",
        "UpgradeScanJob.TriggerSeriesAsync",
        // Left alone on purpose: an archived series still gets its metadata refreshed.
        "MetadataRefreshJob.RefreshSeriesAsync",
    ];

    // Insert their own torrent rows, past the queue backstop, so they check ArchivedAt themselves.
    private static readonly string[] GuardedReleaseMembers = ["SearchAsync", "GrabAsync"];

    [Fact]
    public void Every_queue_entry_point_and_series_job_member_is_on_the_guard_list()
    {
        var queueMembers = typeof(DownloadQueueService)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Where(n => n.StartsWith("Enqueue") || n.StartsWith("Requeue") || n.StartsWith("Retry"))
            .Distinct();
        var jobMembers = typeof(RefreshMonitoredSeriesJob).Assembly.GetTypes()
            .Where(t => t.Namespace == "Maki.Api.Jobs" && !t.IsNested)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                          BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => m.GetParameters().Any(p => p.Name == "seriesId" && p.ParameterType == typeof(int)))
                .Select(m => $"{t.Name}.{m.Name}"))
            .Distinct();

        var releaseMembers = typeof(ReleaseService)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetParameters().Any(p => p.Name == "seriesId" && p.ParameterType == typeof(int)))
            .Select(m => $"ReleaseService.{m.Name}")
            .Distinct();

        var missing = queueMembers.Except(GuardedQueueEntryPoints)
            .Concat(jobMembers.Except(SeriesIdJobMembers))
            .Concat(releaseMembers.Except(GuardedReleaseMembers.Select(m => $"ReleaseService.{m}")))
            .ToList();

        Assert.True(missing.Count == 0,
            $"new series selector or per-series endpoint: add it to the archive guard ({string.Join(", ", missing)})");
    }

    // ---- fakes ----

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    private sealed class PlanToReadSettings : IUserSettingsStore
    {
        public Task<string?> GetAsync(int userId, string key, CancellationToken ct = default) =>
            Task.FromResult<string?>(key == SettingKeys.ScrobblePlanToRead ? "true" : null);

        public Task SetAsync(int userId, string key, string? value, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class PushRecorder : IScrobbleTracker
    {
        public List<(string RemoteId, ScrobbleStatus Status)> Pushes { get; } = [];

        public IEnumerable<string> RemoteIds => Pushes.Select(p => p.RemoteId);

        public string Name => "mangabaka";
        public string Label => "MangaBaka";
        public bool UsesOAuth => false;

        public Task<bool> ConfiguredAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> AuthenticatedAsync(int userId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<string?> UsernameAsync(int userId, CancellationToken ct = default) => Task.FromResult<string?>("fake");

        public Task<RemoteEntry> GetEntryAsync(int userId, string remoteId, CancellationToken ct = default) =>
            Task.FromResult(new RemoteEntry());

        public Task UpdateAsync(int userId, string remoteId, int chapter, int volume, ScrobbleStatus status,
            CancellationToken ct = default, bool keepStatus = false)
        {
            Pushes.Add((remoteId, status));
            return Task.CompletedTask;
        }

        public Task UpdateRatingAsync(int userId, string remoteId, int score, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<ScrobbleCandidate>> SearchAsync(int userId, string title, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScrobbleCandidate>>([]);

        public string EntryUrl(string remoteId) => $"https://example.test/{remoteId}";
    }

    private sealed class SeedRecorder() : SemanticRecommender(
        new Maki.Metadata.Embedding.EmbeddingOptions("", "", "", Maki.Metadata.Embedding.EmbeddingModelProfile.Base),
        new MangaBakaDumpOptions("", ""),
        new Maki.Metadata.Embedding.EmbeddingStore(
            new Maki.Metadata.Embedding.EmbeddingOptions("", "", "", Maki.Metadata.Embedding.EmbeddingModelProfile.Base)),
        null!, null!, Maki.Metadata.RecoGraph.RecoGraphTuning.Default, null!, Maki.Metadata.CoRead.CoReadTuning.Default,
        NullLogger<SemanticRecommender>.Instance)
    {
        public readonly List<IReadOnlyCollection<long>> Seen = [];

        public override bool IsReady() => true;

        public override Task<IReadOnlyDictionary<long, int>> FranchisesAsync(
            IReadOnlyCollection<long> ids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<long, int>>(new Dictionary<long, int>());

        public override Task<IReadOnlyList<MangaBakaRecommendation>> GetSimilarAsync(
            IReadOnlyCollection<long> seedIds, IReadOnlyCollection<long> excludeIds,
            int limit, RecommendationFilters? filters = null, double obscurity = 0,
            IReadOnlyDictionary<long, double>? seedWeights = null,
            IReadOnlyDictionary<long, double>? avoidWeights = null, double diversity = 0,
            Maki.Metadata.Embedding.EmbeddingMath.Weights? weights = null, bool coGraph = true, bool coRead = true,
            bool taste = true, ICollection<Maki.Metadata.Embedding.EmbeddingMath.CandidateFeatures>? features = null,
            CancellationToken ct = default)
        {
            Seen.Add(seedIds);
            IReadOnlyList<MangaBakaRecommendation> picks =
            [
                .. Enumerable.Range(5001, 50).Select(i => new MangaBakaRecommendation(
                    $"{i}", $"Pick {i}", null, null, null, SeriesStatus.Completed, 80, null, [], [], false, null, null, null))
            ];
            return Task.FromResult(picks);
        }
    }

    private sealed class EmptyStore() : MangaBakaLocalStore(
        new MangaBakaDumpOptions("", ""), NullLogger<MangaBakaLocalStore>.Instance)
    {
        public override Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public override Task<IReadOnlyList<MangaBakaRecommendation>> GetRelatedAsync(
            IReadOnlyCollection<long> seedIds, IReadOnlyCollection<long> excludeIds,
            IReadOnlyList<string>? contentRatings = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MangaBakaRecommendation>>([]);
    }
}

/// <summary>
/// The guards that need the upgrade harness: the library-wide upgrade scan, and the download worker
/// refusing to place a file once its series was archived between the claim and the lock.
/// </summary>
[Collection(OneRunPerProcessCollection.Name)]
public sealed class ArchiveGuardWorkerTests : IDisposable
{
    private readonly UpgradeWorld _world = new();

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task UpgradeScan_library_pass_skips_archived_series()
    {
        _world.Seed();
        _world.Db.SeedSeries("Archived", configure: s =>
        {
            s.UpgradeProfileId = _world.ProfileId;
            s.ArchivedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        });

        using var db = _world.Db.NewContext();
        using var batches = _world.Batches();
        var result = await _world.Scanner(db, batches).ScanAllAsync(CancellationToken.None, ignoreGlobalSwitch: true);

        Assert.Equal(1, result.SeriesScanned);
    }

    [Fact]
    public async Task Processor_does_not_place_a_file_into_a_series_archived_while_it_waited_for_the_lock()
    {
        _world.Seed();
        var (chapterId, _) = _world.Chapter(1, withFile: false);
        _world.OfficialPages = UpgradeWorld.InlinePages(3, 100);
        Directory.CreateDirectory(_world.Library);

        int itemId;
        using (var db = _world.Db.NewContext())
        {
            var item = new DownloadQueueItem
            {
                SeriesId = _world.SeriesId, ChapterId = chapterId, SourceMappingId = _world.OfficialMappingId,
                PreferredMappingId = _world.OfficialMappingId, SourceChapterId = "o1",
                QueuedAt = DateTime.UtcNow, Origin = DownloadOrigin.Manual,
            };
            db.DownloadQueue.Add(item);
            db.SaveChanges();
            itemId = item.Id;
        }

        Task<DownloadOutcome> run;
        using (await SeriesLocks.SeriesAsync(_world.SeriesId, CancellationToken.None))
        {
            run = _world.ProcessAsync(itemId);
            await Wait.UntilAsync(() => run.IsCompleted || SeriesLocks.SeriesUsers(_world.SeriesId) >= 2,
                "the download never asked for the series lock to import");
            Assert.False(run.IsCompleted);

            using var db = _world.Db.NewContext();
            db.Series.Single(s => s.Id == _world.SeriesId).ArchivedAt = DateTime.UtcNow;
            db.SaveChanges();
        }

        Assert.Equal(DownloadOutcome.Settled, await run);
        using var check = _world.Db.NewContext();
        Assert.Equal(QueueStatus.Cancelled, check.DownloadQueue.Single(q => q.Id == itemId).Status);
        Assert.Empty(check.ChapterFiles);
        Assert.Null(check.Chapters.Single(c => c.Id == chapterId).ChapterFileId);
        Assert.Empty(Directory.EnumerateFiles(_world.Library, "*.cbz", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Processor_does_not_apply_an_upgrade_to_a_series_archived_while_it_waited_for_the_lock()
    {
        _world.Seed(minWidth: 150);
        var (chapterId, fileId) = _world.Chapter(1, onDisk: true, pages: 4, width: 80);
        _world.OfficialPages = UpgradeWorld.InlinePages(4, 160);
        var path = Path.Combine(_world.Library, "Series", "Series 001.cbz");
        var original = File.ReadAllBytes(path);
        var itemId = _world.QueueUpgrade(chapterId, fileId!.Value);

        Task<DownloadOutcome> run;
        using (await SeriesLocks.SeriesAsync(_world.SeriesId, CancellationToken.None))
        {
            run = _world.ProcessAsync(itemId);
            await Wait.UntilAsync(() => run.IsCompleted || SeriesLocks.SeriesUsers(_world.SeriesId) >= 2,
                "the upgrade never asked for the series lock to apply");
            Assert.False(run.IsCompleted);

            using var db = _world.Db.NewContext();
            db.Series.Single(s => s.Id == _world.SeriesId).ArchivedAt = DateTime.UtcNow;
            db.SaveChanges();
        }

        Assert.Equal(DownloadOutcome.Settled, await run);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(Directory.Exists(Path.Combine(_world.Library, ".maki-trash", _world.SeriesId.ToString())));
        Assert.False(File.Exists(Path.Combine(_world.Library, ".maki", "tmp", $"{itemId}.cbz")));
        using var check = _world.Db.NewContext();
        Assert.Equal(QueueStatus.Cancelled, check.DownloadQueue.Single(q => q.Id == itemId).Status);
        Assert.Equal(fileId, check.Chapters.Single(c => c.Id == chapterId).ChapterFileId);
    }
}
