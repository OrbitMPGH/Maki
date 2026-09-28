using System.IO.Compression;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Kavita;
using Maki.Core.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// A keep-new-standard import leaves the files in the original folder while
/// <see cref="Series.FolderName"/> names the standard one. The stored paths have to follow the files,
/// a rescan must neither drop nor miss them, and rows written under the wrong folder before this was
/// fixed get repointed once.
/// </summary>
public class SplitSeriesFolderTests : IDisposable
{
    private const string Standard = "Berserk (1989)";
    private const string Original = "berserk raws";

    private readonly TestDb _db = new();
    private readonly FakeAppSettings _settings = new();
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "maki-split-" + Guid.NewGuid().ToString("N")[..8]);

    public SplitSeriesFolderTests() => Directory.CreateDirectory(_root);

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

    private Series SeedSeries(string folderName = Standard)
    {
        using var db = _db.NewContext();
        var rootFolder = new RootFolder { Path = _root };
        db.RootFolders.Add(rootFolder);
        db.SaveChanges();

        var series = new Series
        {
            Title = "Berserk",
            SortTitle = "Berserk",
            FolderName = folderName,
            RootFolderId = rootFolder.Id
        };
        db.Series.Add(series);
        db.SaveChanges();

        db.Chapters.AddRange(Enumerable.Range(1, 3).Select(n => new Chapter
        {
            SeriesId = series.Id,
            Number = n,
            Language = "en"
        }));
        db.SaveChanges();
        return series;
    }

    private string WriteComic(string folder, string name)
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry("001.png").Open());
        writer.Write("page");
        return path;
    }

    private async Task<Series> LoadAsync(Maki.Data.MakiDbContext db, int id) =>
        await db.Series.Include(s => s.RootFolder).SingleAsync(s => s.Id == id);

    [Fact]
    public async Task LinkedFilesAreStoredUnderTheFolderTheyAreIn()
    {
        var seeded = SeedSeries();
        var file = WriteComic(Original, "Berserk c001.cbz");

        await using (var db = _db.NewContext())
        {
            var series = await LoadAsync(db, seeded.Id);
            await Service(db).LinkFilesAsync(
                series, Path.Combine(_root, Original), [file], "import", updateComicInfo: false);
        }

        await using var check = _db.NewContext();
        var stored = await check.ChapterFiles.SingleAsync();
        Assert.Equal(Path.Combine(Original, "Berserk c001.cbz"), stored.RelativePath);
    }

    [Fact]
    public async Task RescanKeepsFilesInTheOriginalFolderAndAdoptsNewOnesThere()
    {
        var seeded = SeedSeries();
        var first = WriteComic(Original, "Berserk c001.cbz");
        await using (var db = _db.NewContext())
        {
            var series = await LoadAsync(db, seeded.Id);
            await Service(db).LinkFilesAsync(
                series, Path.Combine(_root, Original), [first], "import", updateComicInfo: false);
        }

        WriteComic(Original, "Berserk c002.cbz");
        WriteComic(Standard, "Berserk c003.cbz");

        RescanResult result;
        await using (var db = _db.NewContext())
        {
            result = await Service(db).RescanSeriesAsync(await LoadAsync(db, seeded.Id));
        }

        Assert.Equal(0, result.Removed);
        await using var check = _db.NewContext();
        var paths = await check.ChapterFiles.Select(f => f.RelativePath).OrderBy(p => p).ToListAsync();
        Assert.Equal(
            [
                Path.Combine(Standard, "Berserk c003.cbz"),
                Path.Combine(Original, "Berserk c001.cbz"),
                Path.Combine(Original, "Berserk c002.cbz")
            ],
            paths);
    }

    [Fact]
    public async Task RescanDoesNotReachIntoAnotherSeriesFolder()
    {
        var seeded = SeedSeries();
        var stray = WriteComic("Vagabond", "Berserk c001.cbz");
        await using (var db = _db.NewContext())
        {
            db.Series.Add(new Series
            {
                Title = "Vagabond", SortTitle = "Vagabond", FolderName = "Vagabond", RootFolderId = seeded.RootFolderId
            });
            // A manual link into the other series' folder.
            db.ChapterFiles.Add(new ChapterFile
            {
                SeriesId = seeded.Id, RelativePath = Path.Combine("Vagabond", "Berserk c001.cbz"), SourceName = "Manual"
            });
            await db.SaveChangesAsync();
        }

        WriteComic("Vagabond", "Vagabond c001.cbz");

        await using (var db = _db.NewContext())
        {
            await Service(db).RescanSeriesAsync(await LoadAsync(db, seeded.Id));
        }

        await using var check = _db.NewContext();
        Assert.DoesNotContain(
            await check.ChapterFiles.Where(f => f.SeriesId == seeded.Id).Select(f => f.RelativePath).ToListAsync(),
            p => p.EndsWith("Vagabond c001.cbz"));
        Assert.True(File.Exists(stray));
    }

    [Fact]
    public async Task RepairRepointsImportedRowsAtTheFolderHoldingTheirFiles()
    {
        var seeded = SeedSeries();
        WriteComic(Original, "Berserk c001.cbz");
        WriteComic(Original, "Berserk c002.cbz");
        // Shares one name, not the set: must not win.
        WriteComic("Other raws", "Berserk c001.cbz");
        await using (var db = _db.NewContext())
        {
            db.ChapterFiles.AddRange(
                new ChapterFile { SeriesId = seeded.Id, RelativePath = Path.Combine(Standard, "Berserk c001.cbz"), SourceName = "import" },
                new ChapterFile { SeriesId = seeded.Id, RelativePath = Path.Combine(Standard, "Berserk c002.cbz"), SourceName = "import" });
            await db.SaveChangesAsync();
        }

        await using (var db = _db.NewContext())
        {
            await new ImportPathRepairService(db, NullLogger<ImportPathRepairService>.Instance).RunOnceAsync();
        }

        await using var check = _db.NewContext();
        var paths = await check.ChapterFiles.Select(f => f.RelativePath).OrderBy(p => p).ToListAsync();
        Assert.Equal(
            [Path.Combine(Original, "Berserk c001.cbz"), Path.Combine(Original, "Berserk c002.cbz")],
            paths);
        Assert.True(await check.AppConfig.AnyAsync(c => c.Key == ImportPathRepairService.MarkerKey));
    }

    [Fact]
    public async Task RepairLeavesRowsAloneWhenTwoFoldersTie()
    {
        var seeded = SeedSeries();
        WriteComic("raws a", "Berserk c001.cbz");
        WriteComic("raws b", "Berserk c001.cbz");
        var stored = Path.Combine(Standard, "Berserk c001.cbz");
        await using (var db = _db.NewContext())
        {
            db.ChapterFiles.Add(new ChapterFile { SeriesId = seeded.Id, RelativePath = stored, SourceName = "import" });
            await db.SaveChangesAsync();
        }

        await using (var db = _db.NewContext())
        {
            await new ImportPathRepairService(db, NullLogger<ImportPathRepairService>.Instance).RunOnceAsync();
        }

        await using var check = _db.NewContext();
        Assert.Equal(stored, (await check.ChapterFiles.SingleAsync()).RelativePath);
    }
}
