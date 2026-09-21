using System.IO.Compression;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Kavita;
using Maki.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// Adopting a volume compilation for a series whose chapter rows carry no volume: the volume only
/// exists in the file name, so the link is the last chance to record it. Plus the one case where a
/// file with no number in its name at all can still be placed.
/// </summary>
public class CbzLinkVolumeTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeAppSettings _settings = new();
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "maki-link-" + Guid.NewGuid().ToString("N")[..8]);

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
            NullLogger<CbzLinkService>.Instance);
    }

    /// <summary>Chapters 1-6 with no volume and no files, the shape a scrape source produces.</summary>
    private Series SeedSeries(int chapterCount = 6)
    {
        using var db = _db.NewContext();
        var rootFolder = new RootFolder { Path = _root };
        db.RootFolders.Add(rootFolder);
        db.SaveChanges();

        var series = new Series
        {
            Title = "Berserk",
            SortTitle = "Berserk",
            FolderName = "Berserk",
            RootFolderId = rootFolder.Id
        };
        db.Series.Add(series);
        db.SaveChanges();
        Directory.CreateDirectory(Path.Combine(_root, "Berserk"));

        db.Chapters.AddRange(Enumerable.Range(1, chapterCount).Select(n => new Chapter
        {
            SeriesId = series.Id,
            Number = n,
            Language = "en"
        }));
        db.SaveChanges();
        return series;
    }

    private string WriteVolume(string fileName, params int[] chapters)
    {
        var path = Path.Combine(_root, "Berserk", fileName);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var chapter in chapters)
        {
            using var writer = new StreamWriter(
                archive.CreateEntry($"Berserk - c{chapter:000} - p001 [Oak].png").Open());
            writer.Write("page");
        }

        return path;
    }

    private async Task LinkAsync(Series series, string path)
    {
        using var db = _db.NewContext();
        await Service(db).LinkFilesAsync(
            db.Series.Single(s => s.Id == series.Id),
            Path.Combine(_root, "Berserk"),
            [path],
            "torrent",
            updateComicInfo: false,
            ct: CancellationToken.None);
    }

    [Fact]
    public async Task A_lone_unrecognized_file_is_linked_to_a_lone_chapter()
    {
        var series = SeedSeries(chapterCount: 1);
        var path = WriteVolume("Look Back.cbz");

        await LinkAsync(series, path);

        using var db = _db.NewContext();
        Assert.NotNull(db.Chapters.Single(c => c.SeriesId == series.Id).ChapterFileId);
    }

    [Fact]
    public async Task A_lone_file_whose_name_carries_a_number_is_left_alone()
    {
        var series = SeedSeries(chapterCount: 1);
        var path = WriteVolume("My Series Ch.50.cbz");

        await LinkAsync(series, path);

        using var db = _db.NewContext();
        Assert.Null(db.Chapters.Single(c => c.SeriesId == series.Id).ChapterFileId);
    }

    [Fact]
    public async Task A_series_with_several_chapters_does_not_adopt_an_unrecognized_file()
    {
        var series = SeedSeries();
        var path = WriteVolume("Look Back.cbz");

        await LinkAsync(series, path);

        using var db = _db.NewContext();
        Assert.All(
            db.Chapters.Where(c => c.SeriesId == series.Id).ToList(),
            c => Assert.Null(c.ChapterFileId));
    }

    [Fact]
    public async Task Chapters_linked_by_contents_take_the_volume_from_the_file_name()
    {
        var series = SeedSeries();
        var path = WriteVolume("Berserk v03 (2019) (Digital) (Oak).cbz", 1, 2, 3, 4, 5, 6);

        await LinkAsync(series, path);

        using var db = _db.NewContext();
        var chapters = db.Chapters.Where(c => c.SeriesId == series.Id).ToList();
        Assert.All(chapters, c => Assert.Equal(3, c.Volume));
        Assert.All(chapters, c => Assert.NotNull(c.ChapterFileId));
    }

    [Fact]
    public async Task A_volume_the_provider_already_assigned_is_not_overwritten()
    {
        var series = SeedSeries();
        using (var seed = _db.NewContext())
        {
            var chapter = seed.Chapters.Single(c => c.SeriesId == series.Id && c.Number == 1);
            chapter.Volume = 1;
            seed.SaveChanges();
        }

        var path = WriteVolume("Berserk v03 (Digital) (Oak).cbz", 1, 2, 3, 4, 5, 6);
        await LinkAsync(series, path);

        using var db = _db.NewContext();
        Assert.Equal(1, db.Chapters.Single(c => c.SeriesId == series.Id && c.Number == 1).Volume);
        Assert.Equal(3, db.Chapters.Single(c => c.SeriesId == series.Id && c.Number == 2).Volume);
    }

    [Fact]
    public async Task A_multi_volume_compilation_assigns_no_volume_at_all()
    {
        var series = SeedSeries();
        var path = WriteVolume("Berserk v03-04 (Digital) (Oak).cbz", 1, 2, 3, 4, 5, 6);

        await LinkAsync(series, path);

        using var db = _db.NewContext();
        var chapters = db.Chapters.Where(c => c.SeriesId == series.Id).ToList();
        Assert.All(chapters, c => Assert.Null(c.Volume));
        Assert.All(chapters, c => Assert.NotNull(c.ChapterFileId));
    }
}
