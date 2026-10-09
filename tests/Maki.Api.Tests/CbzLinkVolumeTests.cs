using System.IO.Compression;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Kavita;
using Maki.Core.Sources;
using Microsoft.EntityFrameworkCore;
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
            TestQuality.Create(registry),
            _settings,
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

    private async Task LinkAsync(Series series, string path, string sourceName = "torrent")
    {
        using var db = _db.NewContext();
        await Service(db).LinkFilesAsync(
            db.Series.Single(s => s.Id == series.Id),
            Path.Combine(_root, "Berserk"),
            [path],
            sourceName,
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

    [Theory]
    [InlineData("import", 0)]
    [InlineData("rescan", 0)]
    [InlineData("torrent:Nyaa", 1)]
    public async Task Only_a_grabbed_torrent_counts_as_a_download(string sourceName, int expectedEvents)
    {
        var series = SeedSeries(chapterCount: 1);
        var path = WriteVolume("Look Back.cbz");

        await LinkAsync(series, path, sourceName);

        using var db = _db.NewContext();
        Assert.Equal(expectedEvents, db.StatsEvents.Count(e => e.Type == StatsEventType.ChapterDownloaded));
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
    public async Task A_single_file_links_the_row_in_its_own_language()
    {
        var series = SeedSeries(chapterCount: 0);
        int englishId, germanId;
        using (var db = _db.NewContext())
        {
            var german = new Chapter { SeriesId = series.Id, Number = 45, Language = "de" };
            db.Chapters.Add(german);
            db.SaveChanges();
            var english = new Chapter { SeriesId = series.Id, Number = 45, Language = "en" };
            db.Chapters.Add(english);
            db.SaveChanges();
            (englishId, germanId) = (english.Id, german.Id);
        }

        await LinkAsync(series, WriteVolume("Berserk Ch.45.cbz"));

        using (var db = _db.NewContext())
        {
            Assert.NotNull(db.Chapters.Single(c => c.Id == englishId).ChapterFileId);
            Assert.Null(db.Chapters.Single(c => c.Id == germanId).ChapterFileId);
        }

        await LinkAsync(series, WriteVolume("Berserk Ch.45 [de].cbz"));

        using var check = _db.NewContext();
        Assert.NotNull(check.Chapters.Single(c => c.Id == germanId).ChapterFileId);
        Assert.NotEqual(
            check.Chapters.Single(c => c.Id == englishId).ChapterFileId,
            check.Chapters.Single(c => c.Id == germanId).ChapterFileId);
    }

    [Fact]
    public async Task A_volume_links_the_rows_in_its_own_language_only()
    {
        var series = SeedSeries(chapterCount: 0);
        using (var db = _db.NewContext())
        {
            db.Chapters.AddRange(
                new Chapter { SeriesId = series.Id, Number = 1, Language = "en" },
                new Chapter { SeriesId = series.Id, Number = 2, Language = "en" },
                new Chapter { SeriesId = series.Id, Number = 1, Language = "de" },
                new Chapter { SeriesId = series.Id, Number = 2, Language = "de" });
            db.SaveChanges();
        }

        await LinkAsync(series, WriteVolume("Berserk v01 (Digital) (Oak).cbz", 1, 2));

        using var check = _db.NewContext();
        var chapters = check.Chapters.Where(c => c.SeriesId == series.Id).ToList();
        Assert.All(chapters.Where(c => c.Language == "en"), c => Assert.NotNull(c.ChapterFileId));
        Assert.All(chapters.Where(c => c.Language == "de"), c => Assert.Null(c.ChapterFileId));
    }

    [Fact]
    public async Task A_volume_tagged_zh_Hans_links_the_older_zh_rows()
    {
        var series = SeedSeries(chapterCount: 0);
        using (var db = _db.NewContext())
        {
            db.Chapters.AddRange(
                new Chapter { SeriesId = series.Id, Number = 1, Language = "en" },
                new Chapter { SeriesId = series.Id, Number = 1, Language = "zh" });
            db.SaveChanges();
        }

        await LinkAsync(series, WriteVolume("Berserk v01 [zh-Hans] (Oak).cbz", 1));

        using var check = _db.NewContext();
        Assert.Null(check.Chapters.Single(c => c.SeriesId == series.Id && c.Language == "en").ChapterFileId);
        Assert.NotNull(check.Chapters.Single(c => c.SeriesId == series.Id && c.Language == "zh").ChapterFileId);
    }

    [Fact]
    public async Task An_untagged_volume_in_a_series_with_no_english_links_by_range_as_before()
    {
        var series = SeedSeries(chapterCount: 0);
        using (var db = _db.NewContext())
        {
            db.Chapters.AddRange(
                new Chapter { SeriesId = series.Id, Number = 1, Volume = 1, Language = "de" },
                new Chapter { SeriesId = series.Id, Number = 1, Volume = 1, Language = "fr" });
            db.SaveChanges();
        }

        await LinkAsync(series, WriteVolume("Berserk v01 (Oak).cbz", 1));

        using var check = _db.NewContext();
        Assert.All(check.Chapters.Where(c => c.SeriesId == series.Id).ToList(), c => Assert.NotNull(c.ChapterFileId));
    }

    [Fact]
    public async Task A_rescan_leaves_a_chapter_on_its_file_when_a_spare_twin_also_matches_it()
    {
        var series = SeedSeries(chapterCount: 1);
        var owner = WriteVolume("Berserk c001.cbz");
        var twin = WriteVolume("Berserk Ch.1.cbz");
        int ownerId;
        using (var db = _db.NewContext())
        {
            var ownerRow = new ChapterFile { SeriesId = series.Id, RelativePath = Path.Combine("Berserk", Path.GetFileName(owner)), DateAdded = DateTime.UtcNow };
            var twinRow = new ChapterFile { SeriesId = series.Id, RelativePath = Path.Combine("Berserk", Path.GetFileName(twin)), DateAdded = DateTime.UtcNow };
            db.ChapterFiles.AddRange(ownerRow, twinRow);
            db.SaveChanges();
            db.Chapters.Single(c => c.SeriesId == series.Id).ChapterFileId = ownerRow.Id;
            db.SaveChanges();
            ownerId = ownerRow.Id;
        }

        for (var run = 0; run < 2; run++)
        {
            using (var db = _db.NewContext())
            {
                await Service(db).RescanSeriesAsync(
                    db.Series.Include(s => s.RootFolder).Single(s => s.Id == series.Id));
            }

            using var check = _db.NewContext();
            Assert.Equal(ownerId, check.Chapters.Single(c => c.SeriesId == series.Id).ChapterFileId);
        }
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
