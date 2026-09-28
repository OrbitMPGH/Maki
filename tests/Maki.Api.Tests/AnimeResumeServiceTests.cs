using System.IO.Compression;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Data;
using Maki.Metadata.MangaBaka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>Builds an <see cref="AnimeResumeService"/> over a test database, for this file and the Home rail tests.</summary>
internal static class AnimeResumeFixture
{
    public const string CoversToFive = "Chap 5 (S1)";

    public static AnimeResumeService Service(TestDb db, MakiDbContext context, ReadingProgressGate gate)
    {
        var scopes = db.ScopeFactory();
        var progress = new ReadingProgressService(context, gate, NullLogger<ReadingProgressService>.Instance);
        var reader = new ReaderService(context, new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance),
            progress, InertKavitaPusher.For(scopes), new ReadingSessionService(context),
            NullLogger<ReaderService>.Instance);
        var signals = new AnimeSignalSyncService(
            scopes,
            new AnimeSignalSources(null!, null!),
            new MangaBakaLocalStore(
                new MangaBakaDumpOptions("", Path.GetTempPath()), new FakeAppSettings(),
                NullLogger<MangaBakaLocalStore>.Instance),
            new FakeAppSettings(),
            new UserSettingsStoreService(scopes),
            NullLogger<AnimeSignalSyncService>.Instance);
        return new AnimeResumeService(context, signals, reader, progress);
    }

    public static void OptIn(TestDb db, int userId) =>
        db.SetUserConfig(userId, (SettingKeys.RecommendationsAnimeSignalsEnabled, "true"));

    public static int SeedAnimeSeries(TestDb db, string title, int? mangaBakaId, string animeEnd = CoversToFive,
        Action<Series>? configure = null) =>
        db.SeedSeries(title, configure: s =>
        {
            s.MangaBakaId = mangaBakaId;
            s.HasAnime = true;
            s.AnimeStart = "Chap 1 (S1)";
            s.AnimeEnd = animeEnd;
            configure?.Invoke(s);
        });

    public static void SeedSignal(TestDb db, int userId, long animeId, long? mangaBakaId,
        AnimeWatchStatus status = AnimeWatchStatus.Completed, int? score = 8, long? aniListMangaId = null)
    {
        using var context = db.NewContext();
        context.AnimeSignals.Add(new AnimeSignal
        {
            UserId = userId,
            Service = "anilist",
            AnimeId = animeId,
            Title = $"Anime {animeId}",
            Score = score,
            Status = status,
            Format = "TV",
            StartDate = new DateOnly(2020, 1, 5),
            EndDate = new DateOnly(2020, 3, 29),
            Episodes = 12,
            Progress = status == AnimeWatchStatus.Completed ? 12 : 3,
            MangaBakaId = mangaBakaId,
            AniListMangaId = aniListMangaId,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        context.SaveChanges();
    }

    public static Dictionary<decimal, int> SeedChapters(TestDb db, int seriesId, params decimal[] numbers)
    {
        using var context = db.NewContext();
        var rows = numbers.Select(n => new Chapter { SeriesId = seriesId, Number = n, Language = "en" }).ToList();
        context.Chapters.AddRange(rows);
        context.SaveChanges();
        return rows.ToDictionary(r => r.Number!.Value, r => r.Id);
    }

    public static void SeedProgress(TestDb db, int userId, int seriesId, int chapterId, bool watched = false)
    {
        using var context = db.NewContext();
        context.ChapterProgress.Add(new ChapterProgress
        {
            UserId = userId,
            SeriesId = seriesId,
            ChapterId = chapterId,
            PageIndex = watched ? 0 : 19,
            PageCount = watched ? 0 : 20,
            Completed = true,
            Watched = watched,
            StartedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        context.SaveChanges();
    }
}

public sealed class AnimeResumeServiceTests : IDisposable
{
    private const int User = 1;

    private readonly TestDb _db = new();
    private readonly ReadingProgressGate _gate = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maki-anime-resume-tests", Guid.NewGuid().ToString("N"));

    public AnimeResumeServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private AnimeResumeService Service(MakiDbContext? context = null) =>
        AnimeResumeFixture.Service(_db, context ?? _db.NewContext(User), _gate);

    private int SeedWatchedSeries(int mangaBakaId = 77)
    {
        AnimeResumeFixture.OptIn(_db, User);
        var seriesId = AnimeResumeFixture.SeedAnimeSeries(_db, "Show", mangaBakaId);
        AnimeResumeFixture.SeedSignal(_db, User, animeId: 1, mangaBakaId: mangaBakaId);
        return seriesId;
    }

    [Fact]
    public async Task Resolves_the_frontier_and_the_chapter_after_it()
    {
        var seriesId = SeedWatchedSeries();
        var chapters = AnimeResumeFixture.SeedChapters(_db, seriesId, 1, 2, 3, 4, 5, 6, 7, 8);

        var resume = await Service().ForSeriesAsync(seriesId, CancellationToken.None);

        Assert.NotNull(resume);
        Assert.Equal(5m, resume.CoveredTo);
        Assert.Equal(6m, resume.ResumeAt);
        Assert.Equal("allSeasons", resume.Basis);
        Assert.Equal(chapters[6m], resume.ResumeChapterId);
        Assert.False(resume.ResumeDownloaded);
        Assert.Equal(5, resume.UnmarkedCount);
        Assert.Null(resume.ReadTo);
    }

    [Fact]
    public async Task Opted_out_returns_nothing_anywhere()
    {
        var seriesId = AnimeResumeFixture.SeedAnimeSeries(_db, "Show", 77);
        AnimeResumeFixture.SeedSignal(_db, User, animeId: 1, mangaBakaId: 77);
        AnimeResumeFixture.SeedChapters(_db, seriesId, 1, 2, 3, 4, 5, 6);
        var service = Service();

        Assert.Null(await service.ForSeriesAsync(seriesId, CancellationToken.None));
        Assert.Null(await service.ForCatalogueAsync(77, "Chap 1 (S1)", AnimeResumeFixture.CoversToFive, 10, CancellationToken.None));
        Assert.Empty(await service.RailAsync(12, CancellationToken.None));
        var (error, _) = await service.ApplyAsync(seriesId, markWatched: true, null, CancellationToken.None);
        Assert.Equal(AnimeResumeError.NotEnabled, error);
    }

    [Fact]
    public async Task Reading_past_the_frontier_suppresses_it()
    {
        var seriesId = SeedWatchedSeries();
        var chapters = AnimeResumeFixture.SeedChapters(_db, seriesId, 1, 2, 3, 4, 5, 6, 7);
        AnimeResumeFixture.SeedProgress(_db, User, seriesId, chapters[6m]);

        Assert.Null(await Service().ForSeriesAsync(seriesId, CancellationToken.None));
    }

    [Fact]
    public async Task A_different_language_row_counts_as_the_same_chapter()
    {
        var seriesId = SeedWatchedSeries();
        var chapters = AnimeResumeFixture.SeedChapters(_db, seriesId, 1, 2, 3, 4, 5, 6);
        using (var context = _db.NewContext())
        {
            context.Chapters.Add(new Chapter { SeriesId = seriesId, Number = 3, Language = "es" });
            context.SaveChanges();
        }
        AnimeResumeFixture.SeedProgress(_db, User, seriesId, chapters[3m]);

        var resume = await Service().ForSeriesAsync(seriesId, CancellationToken.None);

        Assert.NotNull(resume);
        Assert.Equal(4, resume.UnmarkedCount);
        Assert.Equal(3m, resume.ReadTo);
    }

    [Fact]
    public async Task Dismissal_holds_only_while_the_frontier_is_the_same()
    {
        var seriesId = SeedWatchedSeries();
        AnimeResumeFixture.SeedChapters(_db, seriesId, 1, 2, 3, 4, 5, 6, 7, 8);

        Assert.Equal(AnimeResumeError.None, await Service().DismissAsync(seriesId, CancellationToken.None));
        Assert.Null(await Service().ForSeriesAsync(seriesId, CancellationToken.None));

        using (var context = _db.NewContext())
        {
            context.Series.Single(s => s.Id == seriesId).AnimeEnd = "Chap 6 (S1)";
            context.SaveChanges();
        }

        var resume = await Service().ForSeriesAsync(seriesId, CancellationToken.None);
        Assert.NotNull(resume);
        Assert.Equal(6m, resume.CoveredTo);
    }

    [Fact]
    public async Task Undismiss_brings_the_callout_back()
    {
        var seriesId = SeedWatchedSeries();
        AnimeResumeFixture.SeedChapters(_db, seriesId, 1, 2, 3, 4, 5, 6);

        await Service().DismissAsync(seriesId, CancellationToken.None);
        Assert.Equal(AnimeResumeError.None, await Service().UndismissAsync(seriesId, CancellationToken.None));

        Assert.NotNull(await Service().ForSeriesAsync(seriesId, CancellationToken.None));
    }

    [Fact]
    public async Task A_series_without_a_mangabaka_id_matches_on_its_anilist_id()
    {
        AnimeResumeFixture.OptIn(_db, User);
        var seriesId = AnimeResumeFixture.SeedAnimeSeries(_db, "Show", mangaBakaId: null, configure: s => s.AniListId = 500);
        AnimeResumeFixture.SeedSignal(_db, User, animeId: 1, mangaBakaId: null, aniListMangaId: 500);
        AnimeResumeFixture.SeedChapters(_db, seriesId, 1, 2, 3, 4, 5, 6);

        var resume = await Service().ForSeriesAsync(seriesId, CancellationToken.None);

        Assert.NotNull(resume);
        Assert.Equal(5m, resume.CoveredTo);
    }

    [Fact]
    public async Task Another_users_list_is_not_used()
    {
        var other = _db.SeedUser("other");
        AnimeResumeFixture.OptIn(_db, User);
        var seriesId = AnimeResumeFixture.SeedAnimeSeries(_db, "Show", 77);
        AnimeResumeFixture.SeedSignal(_db, other, animeId: 1, mangaBakaId: 77);
        AnimeResumeFixture.SeedChapters(_db, seriesId, 1, 2, 3, 4, 5, 6);

        Assert.Null(await Service().ForSeriesAsync(seriesId, CancellationToken.None));
    }

    [Fact]
    public async Task Catalogue_resume_links_the_library_copy()
    {
        var seriesId = SeedWatchedSeries();

        var resume = await Service().ForCatalogueAsync(
            77, "Chap 1 (S1)", AnimeResumeFixture.CoversToFive, totalChapters: 40, CancellationToken.None);

        Assert.NotNull(resume);
        Assert.Equal(5m, resume.CoveredTo);
        Assert.Equal(6m, resume.ResumeAt);
        Assert.Equal(seriesId, resume.InLibrarySeriesId);
    }

    [Fact]
    public async Task Apply_marks_only_numbered_chapters_up_to_the_frontier_and_keeps_real_reads()
    {
        var seriesId = SeedWatchedSeries();
        var chapters = AnimeResumeFixture.SeedChapters(_db, seriesId, 1, 2, 3, 4, 5, 6, 7);
        int oneShot;
        using (var context = _db.NewContext())
        {
            var row = new Chapter { SeriesId = seriesId, Number = null, IsOneShot = true };
            context.Chapters.Add(row);
            context.SaveChanges();
            oneShot = row.Id;
        }
        AnimeResumeFixture.SeedProgress(_db, User, seriesId, chapters[2m]);

        var (error, result) = await Service().ApplyAsync(seriesId, markWatched: true, null, CancellationToken.None);

        Assert.Equal(AnimeResumeError.None, error);
        Assert.NotNull(result);
        Assert.Equal(4, result.Updated);
        Assert.Equal(5m, result.CoveredTo);
        Assert.Equal(chapters[6m], result.ResumeChapterId);

        using var db = _db.NewContext();
        var progress = db.ChapterProgress.Where(p => p.SeriesId == seriesId).ToDictionary(p => p.ChapterId);
        foreach (var n in new[] { 1m, 3m, 4m, 5m })
        {
            Assert.True(progress[chapters[n]] is { Completed: true, Watched: true });
        }
        Assert.True(progress[chapters[2m]] is { Completed: true, Watched: false, PageIndex: 19 });
        Assert.False(progress.ContainsKey(chapters[6m]));
        Assert.False(progress.ContainsKey(chapters[7m]));
        Assert.False(progress.ContainsKey(oneShot));
        Assert.Empty(db.StatsEvents.ToList());
    }

    [Fact]
    public async Task Apply_clamps_the_range_to_the_frontier_when_caught_up()
    {
        var seriesId = SeedWatchedSeries();
        AnimeResumeFixture.SeedChapters(_db, seriesId, 1, 2, 3, 4, 5, 6, 7, 8);

        var (_, result) = await Service().ApplyAsync(seriesId, markWatched: true, 500m, CancellationToken.None);

        Assert.Equal(5m, result!.CoveredTo);
        Assert.Equal(5, result.Updated);
    }

    [Fact]
    public async Task Apply_clamps_the_range_to_the_end_of_the_next_season()
    {
        AnimeResumeFixture.OptIn(_db, User);
        var seriesId = AnimeResumeFixture.SeedAnimeSeries(_db, "Show", 77, "Chap 5 (S1) / Chap 9 (S2)",
            s => s.AnimeStart = "Chap 1 (S1) / Chap 6 (S2)");
        AnimeResumeFixture.SeedSignal(_db, User, animeId: 1, mangaBakaId: 77);
        AnimeResumeFixture.SeedChapters(_db, seriesId, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11);

        var (_, result) = await Service().ApplyAsync(seriesId, markWatched: true, 500m, CancellationToken.None);

        Assert.Equal(9m, result!.CoveredTo);
        Assert.Equal(9, result.Updated);
    }

    /// <summary>
    /// The regression the baseline raise exists for. Chapters 1-5 are ticked off but not on disk, so
    /// the watched pass alone leaves the high-water mark at 0, and reading chapter 6 would then log
    /// six chapters read in one sitting.
    /// </summary>
    [Fact]
    public async Task Apply_raises_the_mark_so_the_next_real_read_counts_one_chapter()
    {
        var seriesId = SeedWatchedSeries();
        using (var context = _db.NewContext())
        {
            var series = context.Series.Include(s => s.RootFolder).Single(s => s.Id == seriesId);
            series.RootFolder!.Path = _root;
            series.FolderName = "";
            context.SaveChanges();
        }
        AnimeResumeFixture.SeedChapters(_db, seriesId, 1, 2, 3, 4, 5);
        var sixth = LinkDownloadedChapter(seriesId, 6);

        var (error, _) = await Service().ApplyAsync(seriesId, markWatched: true, null, CancellationToken.None);
        Assert.Equal(AnimeResumeError.None, error);

        using (var db = _db.NewContext())
        {
            Assert.Equal(5, db.ReadingStates.Single(r => r.SeriesId == seriesId).MaxChapter);
            Assert.Empty(db.StatsEvents.ToList());
        }

        var readerContext = _db.NewContext(User);
        var reader = new ReaderService(readerContext, new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance),
            new ReadingProgressService(readerContext, _gate, NullLogger<ReadingProgressService>.Instance),
            InertKavitaPusher.For(_db.ScopeFactory()), new ReadingSessionService(readerContext),
            NullLogger<ReaderService>.Instance);
        var slice = await reader.SliceAsync(sixth, CancellationToken.None);
        Assert.NotNull(slice);
        await reader.SaveProgressAsync(slice, slice.PageCount - 1, completed: true,
            ReaderService.TimeReport.None, CancellationToken.None);

        using var after = _db.NewContext();
        var read = Assert.Single(after.StatsEvents.Where(e => e.Type == StatsEventType.ChaptersRead).ToList());
        Assert.Equal(1, read.Value);
    }

    [Fact]
    public async Task Apply_without_marking_only_raises_the_mark()
    {
        var seriesId = SeedWatchedSeries();
        AnimeResumeFixture.SeedChapters(_db, seriesId, 1, 2, 3, 4, 5, 6);

        var (_, result) = await Service().ApplyAsync(seriesId, markWatched: false, null, CancellationToken.None);

        Assert.Equal(0, result!.Updated);
        using var db = _db.NewContext();
        Assert.Empty(db.ChapterProgress.ToList());
        Assert.Equal(5, db.ReadingStates.Single(r => r.SeriesId == seriesId).MaxChapter);
    }

    [Fact]
    public async Task Apply_never_lowers_the_mark()
    {
        var seriesId = SeedWatchedSeries();
        AnimeResumeFixture.SeedChapters(_db, seriesId, 1, 2, 3, 4, 5, 6);
        using (var context = _db.NewContext())
        {
            context.ReadingStates.Add(new ReadingState
            {
                UserId = User, SeriesId = seriesId, Title = "Show", MaxChapter = 9,
                LastProgressAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            context.SaveChanges();
        }

        await Service().ApplyAsync(seriesId, markWatched: false, null, CancellationToken.None);

        using var db = _db.NewContext();
        Assert.Equal(9, db.ReadingStates.Single(r => r.SeriesId == seriesId).MaxChapter);
    }

    private int LinkDownloadedChapter(int seriesId, decimal number)
    {
        var path = Path.Combine(_root, $"{number}.cbz");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "001.jpg", "002.jpg" })
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.WriteByte(0xFF);
            }
        }

        using var db = _db.NewContext();
        var file = new ChapterFile
        {
            SeriesId = seriesId,
            RelativePath = Path.GetFileName(path),
            Size = new FileInfo(path).Length,
            SourceName = "Test",
            DateAdded = DateTime.UtcNow,
        };
        db.ChapterFiles.Add(file);
        db.SaveChanges();

        var chapter = new Chapter { SeriesId = seriesId, Number = number, Language = "en", ChapterFileId = file.Id };
        db.Chapters.Add(chapter);
        db.SaveChanges();
        return chapter.Id;
    }
}
