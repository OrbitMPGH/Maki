using System.IO.Compression;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Kavita;
using Maki.Core.Reading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public class FileRelinkPlannerTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeAppSettings _settings = new();
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "maki-relink-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// A volume whose pages name chapters 1-3 does not contain 4-6, whatever the provider says
    /// volume 1 holds, so those chapters stay on their own files and those files are not superseded.
    /// </summary>
    [Fact]
    public async Task A_partial_volume_takes_only_the_chapters_its_pages_name()
    {
        int seriesId;
        using (var db = _db.NewContext())
        {
            var rootFolder = new RootFolder { Path = _root };
            db.RootFolders.Add(rootFolder);
            db.SaveChanges();
            var series = new Series
            {
                Title = "Berserk", SortTitle = "Berserk", FolderName = "Berserk", RootFolderId = rootFolder.Id
            };
            db.Series.Add(series);
            db.SaveChanges();
            seriesId = series.Id;

            foreach (var number in Enumerable.Range(1, 6))
            {
                var relative = Path.Combine("Berserk", $"Berserk Vol.1 Ch.{number}.cbz");
                WriteCbz(Path.Combine(_root, relative), [$"Berserk - c00{number} - p001.png"]);
                var file = new ChapterFile
                {
                    SeriesId = series.Id, RelativePath = relative, SourceName = "mangadex", DateAdded = DateTime.UtcNow
                };
                db.ChapterFiles.Add(file);
                db.SaveChanges();
                db.Chapters.Add(new Chapter
                {
                    SeriesId = series.Id, Number = number, Volume = 1, Language = "en", ChapterFileId = file.Id
                });
                db.SaveChanges();
            }
        }

        WriteCbz(
            Path.Combine(_root, "Berserk", "Berserk v01 (Digital) (1r0n).cbz"),
            [.. Enumerable.Range(1, 3).Select(c => $"Berserk - c{c:000} - p001 [Oak].png")]);

        using var context = _db.NewContext();
        var loaded = await context.Series.Include(s => s.RootFolder).SingleAsync(s => s.Id == seriesId);
        var scans = new KavitaScanService(
            new KavitaClient(new StubHttpClientFactory("{}")), _settings, _db.ScopeFactory(),
            NullLogger<KavitaScanService>.Instance);
        var planner = new FileRelinkPlanner(
            context, new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance), scans,
            NullLogger<FileRelinkPlanner>.Instance);

        var plan = await planner.PlanAsync(loaded, RelinkOptions.None);

        Assert.Equal(3, plan.SupersededCount);
        var volume = Assert.Single(plan.Files, f => f.IsVolume);
        Assert.Equal(["1", "2", "3"], volume.Chapters);
        var chapterFour = Assert.Single(plan.Files, f => f.FileName == "Berserk Vol.1 Ch.4.cbz");
        Assert.False(chapterFour.Superseded);
        Assert.Empty(chapterFour.Loses);
    }

    private static void WriteCbz(string path, IReadOnlyList<string> pageNames)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var name in pageNames)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write("page");
        }
    }
}
