using System.IO.Compression;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Kavita;
using Maki.Core.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// Files on disk become chapters when no source lists them, and a volume entry hands its file and
/// its reads to the chapters a source lists later.
/// </summary>
public class ChapterFromFilesTests : IDisposable
{
    private const int Reader = 1;
    private readonly TestDb _db = new TestDb().WithDistinctSeriesIds();
    private readonly FakeAppSettings _settings = new();
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "maki-fromfiles-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private CbzLinkService Service(Maki.Data.MakiDbContext db)
    {
        var registry = new SourceRegistry([]);
        return new CbzLinkService(
            db,
            registry,
            new KavitaScanService(
                new KavitaClient(new StubHttpClientFactory("{}")),
                _settings,
                _db.ScopeFactory(),
                NullLogger<KavitaScanService>.Instance),
            new StatsEventService(db),
            new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance),
            new SourceAvailability(_settings, registry),
            TestQuality.Create(registry),
            _settings,
            NullLogger<CbzLinkService>.Instance,
            new ChapterFromFilesService(
                db,
                new ReadingProgressService(db, new ReadingProgressGate(), NullLogger<ReadingProgressService>.Instance),
                _settings,
                NullLogger<ChapterFromFilesService>.Instance));
    }

    private Series SeedSeries()
    {
        using var db = _db.NewContext();
        var rootFolder = new RootFolder { Path = _root };
        db.RootFolders.Add(rootFolder);
        db.SaveChanges();

        var series = new Series { Title = "Berserk", SortTitle = "Berserk", FolderName = "Berserk", RootFolderId = rootFolder.Id };
        db.Series.Add(series);
        db.SaveChanges();
        Directory.CreateDirectory(Path.Combine(_root, "Berserk"));
        return series;
    }

    /// <summary>A CBZ whose pages carry the given chapter markers, or one unmarked page without any.</summary>
    private string WriteFile(string fileName, params int[] markers)
    {
        var path = Path.Combine(_root, "Berserk", fileName);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var pages = markers.Length == 0 ? ["001.png"] : markers.Select(m => $"Berserk - c{m:000} - p001 [Oak].png");
        foreach (var page in pages)
        {
            using var writer = new StreamWriter(archive.CreateEntry(page).Open());
            writer.Write("page");
        }

        return path;
    }

    private async Task LinkAsync(Series series, params string[] paths)
    {
        using var db = _db.NewContext();
        await Service(db).LinkFilesAsync(
            db.Series.Single(s => s.Id == series.Id), Path.Combine(_root, "Berserk"), paths, "import",
            updateComicInfo: false, ct: CancellationToken.None);
    }

    private async Task RescanAsync(Series series)
    {
        using var db = _db.NewContext();
        await Service(db).RescanSeriesAsync(db.Series.Include(s => s.RootFolder).Single(s => s.Id == series.Id));
    }

    private List<Chapter> ChaptersOf(Series series)
    {
        using var db = _db.NewContext();
        return db.Chapters.Where(c => c.SeriesId == series.Id).ToList().OrderBy(c => c.Number).ThenBy(c => c.Volume).ToList();
    }

    private void SeedSourceListed(Series series, decimal number)
    {
        using var db = _db.NewContext();
        var mapping = new SourceMapping { SeriesId = series.Id, SourceName = "fake", SourceSeriesId = "s", Url = "u", Enabled = true };
        var chapter = new Chapter { SeriesId = series.Id, Number = number, Language = "en" };
        db.SourceMappings.Add(mapping);
        db.Chapters.Add(chapter);
        db.SaveChanges();
        db.ChapterSourceLinks.Add(new ChapterSourceLink { ChapterId = chapter.Id, SourceMappingId = mapping.Id, SourceChapterId = $"c{number}" });
        db.SaveChanges();
    }

    [Fact]
    public async Task Chapter_files_with_no_chapter_become_chapters_under_their_number()
    {
        var series = SeedSeries();

        await LinkAsync(series, WriteFile("Berserk Ch.001.cbz"), WriteFile("Berserk Ch.002.5.cbz"));

        var chapters = ChaptersOf(series);
        Assert.Equal([1m, 2.5m], chapters.Select(c => c.Number!.Value));
        Assert.All(chapters, c => Assert.NotNull(c.ChapterFileId));
        Assert.All(chapters, c => Assert.False(c.IsOneShot));
    }

    [Fact]
    public async Task Chapter_zero_and_unnumbered_files_get_no_chapter()
    {
        var series = SeedSeries();

        await LinkAsync(series, WriteFile("Berserk Ch.000.cbz"), WriteFile("Berserk Artbook.cbz"));

        Assert.Empty(ChaptersOf(series));
    }

    [Fact]
    public async Task A_number_the_series_already_has_is_not_built_twice()
    {
        var series = SeedSeries();
        await LinkAsync(series, WriteFile("Berserk Ch.001.cbz"));

        await LinkAsync(series, WriteFile("Berserk Ch.001 (v2).cbz"));

        Assert.Single(ChaptersOf(series));
    }

    [Fact]
    public async Task A_volume_with_chapter_markers_becomes_its_chapters()
    {
        var series = SeedSeries();

        await LinkAsync(series, WriteFile("Berserk v01.cbz", 1, 2, 3));

        var chapters = ChaptersOf(series);
        Assert.Equal([1m, 2m, 3m], chapters.Select(c => c.Number!.Value));
        Assert.All(chapters, c => Assert.Equal(1, c.Volume));
        Assert.Single(chapters.Select(c => c.ChapterFileId).Distinct());
    }

    [Fact]
    public async Task A_volume_without_markers_becomes_one_entry_per_volume()
    {
        var series = SeedSeries();

        await LinkAsync(series, WriteFile("Berserk v01.cbz"), WriteFile("Berserk v02-03.cbz"));

        var chapters = ChaptersOf(series);
        Assert.Equal([1, 2, 3], chapters.Select(c => c.Volume!.Value));
        Assert.All(chapters, c => Assert.True(Chapter.IsVolumeEntry(c)));
        Assert.All(chapters, c => Assert.False(c.Wanted));
        Assert.Equal(chapters[1].ChapterFileId, chapters[2].ChapterFileId);
    }

    [Fact]
    public async Task A_volume_without_markers_stays_unlinked_once_a_source_lists_chapters()
    {
        var series = SeedSeries();
        SeedSourceListed(series, 1);

        await LinkAsync(series, WriteFile("Berserk v05.cbz"));

        Assert.DoesNotContain(ChaptersOf(series), Chapter.IsVolumeEntry);
    }

    [Fact]
    public async Task A_source_listed_chapter_takes_a_numbered_row_over_without_duplicating_it()
    {
        var series = SeedSeries();
        await LinkAsync(series, WriteFile("Berserk Ch.001.cbz"));
        var built = ChaptersOf(series).Single();

        Assert.True(ChapterIdentity.Matches(built, new SourceChapter("fake", "s", "c1", "1", 1m, null, null, "en", null)));
    }

    [Fact]
    public async Task Chapters_listed_for_a_volume_take_its_entry_and_its_reads_over()
    {
        var series = SeedSeries();
        await LinkAsync(series, WriteFile("Berserk v01.cbz"));
        var entry = ChaptersOf(series).Single();
        using (var db = _db.NewContext())
        {
            db.ChapterProgress.Add(new ChapterProgress
            {
                UserId = Reader, SeriesId = series.Id, ChapterId = entry.Id, Completed = true, PageCount = 200,
                StartedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow
            });
            db.Chapters.AddRange(Enumerable.Range(1, 3).Select(n => new Chapter { SeriesId = series.Id, Number = n, Volume = 1, Language = "en" }));
            db.SaveChanges();
        }

        await RescanAsync(series);

        var chapters = ChaptersOf(series);
        Assert.Equal([1m, 2m, 3m], chapters.Select(c => c.Number!.Value));
        Assert.All(chapters, c => Assert.Equal(entry.ChapterFileId, c.ChapterFileId));
        using var check = _db.NewContext();
        var progress = check.ChapterProgress.Where(p => p.SeriesId == series.Id).ToList();
        Assert.Equal(3, progress.Count);
        Assert.All(progress, p => Assert.True(p.Completed));
        Assert.Equal(3, check.ReadingStates.Single(r => r.SeriesId == series.Id).MaxChapter);
    }

    [Fact]
    public async Task A_sync_that_lists_a_volumes_chapters_hands_them_its_file_before_anything_queues_them()
    {
        var series = SeedSeries();
        await LinkAsync(series, WriteFile("Berserk v01.cbz"));
        var fileId = ChaptersOf(series).Single().ChapterFileId;
        using (var seed = _db.NewContext())
        {
            seed.SourceMappings.Add(new SourceMapping { SeriesId = series.Id, SourceName = "fake", SourceSeriesId = "s", Url = "u", Enabled = true });
            seed.SaveChanges();
        }

        var source = new FakeSource
        {
            Name = "fake",
            OnListChapters = _ => Enumerable.Range(1, 3)
                .Select(n => new SourceChapter("fake", "s", $"c{n}", n.ToString(), n, 1, null, "en", null))
                .ToList()
        };
        var registry = new SourceRegistry([source]);
        using var db = _db.NewContext();
        var sync = new ChapterSyncService(
            db, registry,
            new DownloadQueueService(null!, TimeProvider.System, null!, NullLogger<DownloadQueueService>.Instance),
            new SourceAvailability(_settings, registry),
            new SourceChapterListCache(TimeProvider.System, NullLogger<SourceChapterListCache>.Instance),
            _settings, NullLogger<ChapterSyncService>.Instance,
            cbzLinks: Service(db));

        await sync.SyncSeriesAsync(series.Id);

        var chapters = ChaptersOf(series);
        Assert.Equal([1m, 2m, 3m], chapters.Select(c => c.Number!.Value));
        Assert.All(chapters, c => Assert.Equal(fileId, c.ChapterFileId));
    }

    [Fact]
    public async Task A_volume_entry_with_no_listed_chapters_keeps_its_file_on_rescan()
    {
        var series = SeedSeries();
        await LinkAsync(series, WriteFile("Berserk v01.cbz"));

        await RescanAsync(series);

        var entry = Assert.Single(ChaptersOf(series));
        Assert.True(Chapter.IsVolumeEntry(entry));
        Assert.NotNull(entry.ChapterFileId);
    }
}
