using System.IO.Compression;
using System.Security.Cryptography;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Kavita;
using Maki.Core.Sources;
using Maki.Core.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// A rescan adopts files it finds and rewrites their ComicInfo.xml, so a directory symlink planted in
/// a series folder must not lead it to archives outside the library.
/// </summary>
public class CbzLinkSymlinkTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeAppSettings _settings = new();
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "maki-symlink-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _outside =
        Path.Combine(Path.GetTempPath(), "maki-symlink-outside-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        _db.Dispose();
        TestLinks.UnlinkDirectory(Path.Combine(_root, "Berserk", "linked"));
        foreach (var dir in new[] { _root, _outside })
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
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

    private static void WriteComic(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry("001.png").Open());
        writer.Write("page");
    }

    [Fact]
    public async Task Rescan_does_not_adopt_or_rewrite_an_archive_behind_a_directory_symlink()
    {
        int seriesId;
        using (var db = _db.NewContext())
        {
            var rootFolder = new RootFolder { Path = _root };
            db.RootFolders.Add(rootFolder);
            db.SaveChanges();
            var series = new Series { Title = "Berserk", SortTitle = "Berserk", FolderName = "Berserk", RootFolderId = rootFolder.Id };
            db.Series.Add(series);
            db.SaveChanges();
            db.Chapters.AddRange(Enumerable.Range(1, 2).Select(n => new Chapter { SeriesId = series.Id, Number = n, Language = "en" }));
            db.SaveChanges();
            seriesId = series.Id;
        }

        WriteComic(Path.Combine(_root, "Berserk", "Berserk c002.cbz"));
        var external = Path.Combine(_outside, "Berserk c001.cbz");
        WriteComic(external);
        var before = SHA256.HashData(await File.ReadAllBytesAsync(external));
        var writtenBefore = File.GetLastWriteTimeUtc(external);
        if (!TestLinks.TryLinkDirectory(Path.Combine(_root, "Berserk", "linked"), _outside))
        {
            return;
        }

        using (var db = _db.NewContext())
        {
            await Service(db).RescanSeriesAsync(
                await db.Series.Include(s => s.RootFolder).SingleAsync(s => s.Id == seriesId));
        }

        using var check = _db.NewContext();
        Assert.Equal(new[] { Path.Combine("Berserk", "Berserk c002.cbz") },
            await check.ChapterFiles.Select(f => f.RelativePath).ToListAsync());
        Assert.Null((await check.Chapters.SingleAsync(c => c.SeriesId == seriesId && c.Number == 1)).ChapterFileId);
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(external)));
        Assert.Equal(writtenBefore, File.GetLastWriteTimeUtc(external));
    }

    /// <summary>A series with chapter 1 linked to a file directly in the root, the way a manual link can leave it.</summary>
    private int SeedRootLevelRow(string fileName)
    {
        using var db = _db.NewContext();
        var rootFolder = new RootFolder { Path = _root };
        db.RootFolders.Add(rootFolder);
        db.SaveChanges();
        var series = new Series { Title = "Berserk", SortTitle = "Berserk", FolderName = "Berserk", RootFolderId = rootFolder.Id };
        db.Series.Add(series);
        db.SaveChanges();
        var file = new ChapterFile { SeriesId = series.Id, RelativePath = fileName, DateAdded = DateTime.UtcNow };
        db.ChapterFiles.Add(file);
        db.SaveChanges();
        db.Chapters.Add(new Chapter { SeriesId = series.Id, Number = 1, Language = "en", ChapterFileId = file.Id });
        db.SaveChanges();
        return series.Id;
    }

    private async Task RescanAsync(int seriesId)
    {
        using var db = _db.NewContext();
        await Service(db).RescanSeriesAsync(await db.Series.Include(s => s.RootFolder).SingleAsync(s => s.Id == seriesId));
    }

    [Fact]
    public async Task Rescan_keeps_a_root_level_row_when_the_root_lists_empty()
    {
        // An unmounted share leaves an empty mount point: that says nothing about the file.
        Directory.CreateDirectory(_root);
        var seriesId = SeedRootLevelRow("Berserk c001.cbz");

        await RescanAsync(seriesId);

        using var check = _db.NewContext();
        Assert.Single(check.ChapterFiles);
        Assert.NotNull((await check.Chapters.SingleAsync(c => c.SeriesId == seriesId)).ChapterFileId);
    }

    [Fact]
    public async Task Rescan_keeps_a_root_level_row_whose_file_is_a_symlink()
    {
        var external = Path.Combine(_outside, "Berserk c001.cbz");
        WriteComic(external);
        Directory.CreateDirectory(_root);
        if (!TestLinks.TryLinkFile(Path.Combine(_root, "Berserk c001.cbz"), external))
        {
            return;
        }

        var seriesId = SeedRootLevelRow("Berserk c001.cbz");

        await RescanAsync(seriesId);

        using var check = _db.NewContext();
        Assert.Single(check.ChapterFiles);
        Assert.NotNull((await check.Chapters.SingleAsync(c => c.SeriesId == seriesId)).ChapterFileId);
    }
}
