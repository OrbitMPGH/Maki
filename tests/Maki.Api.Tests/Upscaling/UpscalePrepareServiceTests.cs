using Maki.Api.Configuration;
using Maki.Api.Services;
using Maki.Api.Services.Upscaling;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Quality;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests.Upscaling;

public sealed class UpscalePrepareServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
    private const string Always = """{"enhance":"always","enhanceMethod":"server:span-s"}""";
    private const string Auto = """{"enhance":"auto","enhanceMethod":"server:span-s"}""";

    private readonly TestDb _db = new();
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "maki-prepare-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly FakeAppSettings _settings = new FakeAppSettings()
        .Set(SettingKeys.UpscalingEnabled, "true")
        .Set(SettingKeys.UpscalingModels, "span-s")
        .Set(SettingKeys.UpscalingPrepare, "true");
    private readonly UpscaleModelCatalog _catalog = new();
    private readonly UpscaleModelStore _store;
    private readonly UpscaleQueue _queue = new();
    private readonly UpscaleCache _cache;
    private readonly string _root;
    private readonly int _rootId;
    private readonly int _user;

    public UpscalePrepareServiceTests()
    {
        _paths = new AppPaths(_configDir);
        _store = UpscaleTestKit.StoreWithSpanS(_paths, _settings, _catalog);
        _cache = new UpscaleCache(_paths, _settings, NullLogger<UpscaleCache>.Instance);
        _root = Path.Combine(_configDir, "library");
        using var db = _db.NewContext();
        var folder = new RootFolder { Path = _root };
        db.RootFolders.Add(folder);
        db.SaveChanges();
        _rootId = folder.Id;
        _user = _db.SeedUser("reader");
        _db.SetUserConfig(_user, (SettingKeys.ReaderPrefs, Always));
    }

    public void Dispose()
    {
        _db.Dispose();
        UpscaleTestKit.TryDelete(_configDir);
    }

    private sealed class Clock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }

    private UpscalePrepareService Service()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _db.NewContext());
        services.AddScoped<ContinueReadingService>();
        services.AddScoped(sp =>
        {
            var db = sp.GetRequiredService<MakiDbContext>();
            return new ReaderService(db, new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance), null!, null!,
                new ReadingSessionService(db), NullLogger<ReaderService>.Instance);
        });
        var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new UpscalePrepareService(scopes, _settings, _store, _queue, _cache, new Clock(Now),
            NullLogger<UpscalePrepareService>.Instance);
    }

    private int AddSeries(string title)
    {
        using var db = _db.NewContext();
        var series = new Series { Title = title, SortTitle = title.ToLowerInvariant(), FolderName = title, RootFolderId = _rootId };
        db.Series.Add(series);
        db.SaveChanges();
        return series.Id;
    }

    private int AddChapter(int seriesId, int number, int pages = 4, QualityTier tier = QualityTier.Unknown)
    {
        var name = $"s{seriesId}c{number}.cbz";
        var path = Path.Combine(_root, "S", name);
        UpscaleTestKit.WriteCbz(path, pages);
        using var db = _db.NewContext();
        var file = new ChapterFile
        {
            SeriesId = seriesId, RelativePath = $"S/{name}", Size = new FileInfo(path).Length, SourceName = "test",
            DateAdded = Now, Tier = tier
        };
        db.ChapterFiles.Add(file);
        db.SaveChanges();
        var chapter = new Chapter { SeriesId = seriesId, Number = number, ChapterFileId = file.Id };
        db.Chapters.Add(chapter);
        db.SaveChanges();
        return chapter.Id;
    }

    /// <summary>Progress on <paramref name="chapterId"/>, then the series' status and settings set by hand.</summary>
    private void Read(int seriesId, int chapterId, DateTime at, bool completed = true, int page = 3,
        ReadingStatus status = ReadingStatus.Reading, string? prefs = null)
    {
        using (var db = _db.NewContext(_user))
        {
            db.ChapterProgress.Add(new ChapterProgress
            {
                UserId = _user, SeriesId = seriesId, ChapterId = chapterId, PageIndex = page, PageCount = 4,
                Completed = completed, StartedAt = at, UpdatedAt = at, CompletedAt = completed ? at : null
            });
            db.SaveChanges();
        }

        using (var db = _db.NewContext(_user))
        {
            var state = db.UserSeriesStates.FirstOrDefault(s => s.SeriesId == seriesId);
            if (state is null)
            {
                state = new UserSeriesState { UserId = _user, SeriesId = seriesId };
                db.UserSeriesStates.Add(state);
            }

            state.Status = status;
            state.ReaderPrefsJson = prefs;
            db.SaveChanges();
        }

        using (var db = _db.NewContext(_user))
        {
            db.ChapterProgress.Where(p => p.ChapterId == chapterId).ExecuteUpdate(p => p.SetProperty(x => x.UpdatedAt, at));
        }
    }

    private List<(int File, int Page)> Queued()
    {
        var pages = new List<(int, int)>();
        while (_queue.TryDequeue() is { } job)
        {
            pages.Add((job.Key.ChapterFileId, job.Key.AbsIndex));
            _queue.Complete(job, Maki.Core.Images.Upscaling.UpscaleOutcome.Ready, null);
        }

        return pages;
    }

    private int FileOf(int chapterId)
    {
        using var db = _db.NewContext();
        return db.Chapters.Where(c => c.Id == chapterId).Select(c => c.ChapterFileId!.Value).Single();
    }

    [Fact]
    public async Task Queues_the_next_chapter_of_a_series_being_read()
    {
        var series = AddSeries("Read");
        var first = AddChapter(series, 1);
        var second = AddChapter(series, 2);
        AddChapter(series, 3);
        Read(series, first, Now.AddDays(-1));

        var plan = await Service().PlanAsync(default);

        Assert.Equal(new PrepareSnapshot(1, 4, 4), plan);
        var file = FileOf(second);
        Assert.Equal([(file, 0), (file, 1), (file, 2), (file, 3)], Queued());
    }

    [Fact]
    public async Task A_part_read_chapter_is_prepared_from_the_page_reached()
    {
        var series = AddSeries("Part");
        var first = AddChapter(series, 1);
        Read(series, first, Now.AddDays(-1), completed: false, page: 2);

        await Service().PlanAsync(default);

        var file = FileOf(first);
        Assert.Equal([(file, 2), (file, 3)], Queued());
    }

    [Fact]
    public async Task Leaves_out_Auto_old_reads_and_series_not_set_to_Reading()
    {
        var auto = AddSeries("Auto");
        Read(auto, AddChapter(auto, 1), Now.AddDays(-1), prefs: Auto);
        AddChapter(auto, 2);
        var old = AddSeries("Old");
        Read(old, AddChapter(old, 1), Now.AddDays(-30));
        AddChapter(old, 2);
        var held = AddSeries("Held");
        Read(held, AddChapter(held, 1), Now.AddDays(-1), status: ReadingStatus.OnHold);
        AddChapter(held, 2);

        var plan = await Service().PlanAsync(default);

        Assert.Equal(PrepareSnapshot.Empty, plan);
        Assert.Empty(Queued());
    }

    [Fact]
    public async Task Takes_more_chapters_breadth_first_and_skips_a_skipped_tier()
    {
        _settings.Set(SettingKeys.UpscalingPrepareChapters, "2");
        var older = AddSeries("Older");
        Read(older, AddChapter(older, 1), Now.AddDays(-3));
        var olderNext = AddChapter(older, 2, pages: 1);
        AddChapter(older, 3, pages: 1, tier: QualityTier.Volume);
        var newer = AddSeries("Newer");
        Read(newer, AddChapter(newer, 1), Now.AddDays(-1));
        var newerNext = AddChapter(newer, 2, pages: 1);
        var newerAfter = AddChapter(newer, 3, pages: 1);

        await Service().PlanAsync(default);

        Assert.Equal([(FileOf(newerNext), 0), (FileOf(olderNext), 0), (FileOf(newerAfter), 0)], Queued());
    }

    [Fact]
    public async Task Cached_pages_are_held_and_not_queued_again()
    {
        var series = AddSeries("Cached");
        Read(series, AddChapter(series, 1), Now.AddDays(-1));
        var next = AddChapter(series, 2, pages: 2);
        var service = Service();
        await service.PlanAsync(default);
        foreach (var (file, page) in Queued())
        {
            using var db = _db.NewContext();
            var slice = await new ReaderService(db, new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance), null!, null!,
                new ReadingSessionService(db), NullLogger<ReaderService>.Instance).PageSliceAsync(next, default);
            await _cache.WriteAsync(new UpscaleWorkKey(file, slice!.ArchiveVersion, page, "span-s"), new byte[100], "webp", default);
        }

        var plan = await service.PlanAsync(default);

        Assert.Equal(new PrepareSnapshot(1, 2, 0), plan);
        Assert.Empty(Queued());
        Assert.Equal((200L, 2, 100L), _cache.HeldStats());
    }

    [Fact]
    public async Task Turning_it_off_lets_go_of_everything()
    {
        var series = AddSeries("Off");
        Read(series, AddChapter(series, 1), Now.AddDays(-1));
        AddChapter(series, 2);
        var service = Service();
        await service.PlanAsync(default);

        _settings.Set(SettingKeys.UpscalingPrepare, "false");
        var plan = await service.PlanAsync(default);

        Assert.Equal(PrepareSnapshot.Empty, plan);
        Assert.Empty(Queued());
    }

    [Fact]
    public void Breadth_first_takes_every_series_first_chapter_before_any_second_one()
    {
        UpscalePrepareService.Target T(int user, int chapter) => new(user, chapter, "span-s", 0);
        List<List<List<UpscalePrepareService.Target>>> perUser =
        [
            [[T(1, 10), T(1, 11)], [T(1, 20)]],
            [[T(2, 30), T(2, 31)]],
        ];

        var order = UpscalePrepareService.BreadthFirst(perUser, 2).Select(t => t.ChapterId);

        Assert.Equal([10, 30, 20, 11, 31], order);
    }
}
