using Maki.Api.Services;
using Maki.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// One file, two rows: the shape a re-run torrent import left behind. The repair keeps the oldest
/// row, moves every chapter onto it and drops the rest, and does so once.
/// </summary>
public sealed class ChapterFileDuplicateRepairTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private ChapterFileDuplicateRepairService Service(ReaderArchiveCache? archives = null) =>
        new(_db.NewContext(), archives ?? new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance),
            NullLogger<ChapterFileDuplicateRepairService>.Instance);

    private int SeedSeries(string title)
    {
        using var db = _db.NewContext();
        var root = db.RootFolders.FirstOrDefault();
        if (root is null)
        {
            root = new RootFolder { Path = "/library" };
            db.RootFolders.Add(root);
            db.SaveChanges();
        }

        var series = new Series { Title = title, SortTitle = title, FolderName = title, RootFolderId = root.Id };
        db.Series.Add(series);
        db.SaveChanges();
        return series.Id;
    }

    private int AddFile(int seriesId, string relativePath)
    {
        using var db = _db.NewContext();
        var file = new ChapterFile
        {
            SeriesId = seriesId,
            RelativePath = relativePath,
            SourceName = "torrent:Nyaa.si (Manga)",
            DateAdded = DateTime.UtcNow
        };
        db.ChapterFiles.Add(file);
        db.SaveChanges();
        return file.Id;
    }

    private int AddChapter(int seriesId, decimal number, int? fileId)
    {
        using var db = _db.NewContext();
        var chapter = new Chapter { SeriesId = seriesId, Number = number, Language = "en", ChapterFileId = fileId };
        db.Chapters.Add(chapter);
        db.SaveChanges();
        return chapter.Id;
    }

    [Fact]
    public async Task Merges_duplicates_onto_the_lowest_id_and_repoints_their_chapters()
    {
        var series = SeedSeries("Berserk");
        var keep = AddFile(series, @"Berserk\Berserk v01 (2022) (Digital) (1r0n).cbz");
        var extra = AddFile(series, "Berserk/Berserk v01 (2022) (Digital) (1r0n).cbz");
        var unrelated = AddFile(series, "Berserk/Berserk v02 (2022) (Digital) (1r0n).cbz");
        var onKeep = AddChapter(series, 1, keep);
        var onExtra = AddChapter(series, 2, extra);
        var onOther = AddChapter(series, 3, unrelated);

        var archives = new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance);
        await Service(archives).RunOnceAsync();

        using var db = _db.NewContext();
        var files = db.ChapterFiles.Where(f => f.SeriesId == series).Select(f => f.Id).OrderBy(id => id).ToList();
        Assert.Equal([keep, unrelated], files);
        Assert.Equal(keep, db.Chapters.Single(c => c.Id == onKeep).ChapterFileId);
        Assert.Equal(keep, db.Chapters.Single(c => c.Id == onExtra).ChapterFileId);
        Assert.Equal(unrelated, db.Chapters.Single(c => c.Id == onOther).ChapterFileId);
        Assert.True(db.AppConfig.Any(c => c.Key == ChapterFileDuplicateRepairService.MarkerKey));
    }

    [Fact]
    public async Task Paths_differing_only_in_case_are_two_files_on_a_case_sensitive_host()
    {
        var series = SeedSeries("Berserk");
        var lower = AddFile(series, "Berserk/berserk v01.cbz");
        var upper = AddFile(series, "Berserk/Berserk v01.cbz");

        await Service().RunOnceAsync();

        using var db = _db.NewContext();
        var files = db.ChapterFiles.Where(f => f.SeriesId == series).Select(f => f.Id).OrderBy(id => id).ToList();
        Assert.Equal(OperatingSystem.IsWindows() ? [lower] : [lower, upper], files);
    }

    [Fact]
    public async Task The_same_path_in_two_series_is_two_files()
    {
        var a = SeedSeries("Berserk");
        var b = SeedSeries("Berserk (2016)");
        AddFile(a, "Berserk/Berserk v01.cbz");
        AddFile(b, "Berserk/Berserk v01.cbz");

        var (groups, removed) = await Service().MergeAsync();

        Assert.Equal(0, groups);
        Assert.Equal(0, removed);
        using var db = _db.NewContext();
        Assert.Equal(2, db.ChapterFiles.Count());
    }

    [Fact]
    public async Task Runs_once_only()
    {
        var series = SeedSeries("Berserk");
        AddFile(series, "Berserk/Berserk v01.cbz");
        AddFile(series, "Berserk/Berserk v01.cbz");
        await Service().RunOnceAsync();

        // A pair written after the marker is not this pass's to clean up.
        AddFile(series, "Berserk/Berserk v02.cbz");
        AddFile(series, "Berserk/Berserk v02.cbz");
        await Service().RunOnceAsync();

        using var db = _db.NewContext();
        Assert.Equal(3, db.ChapterFiles.Count(f => f.SeriesId == series));
        Assert.Equal(1, await db.AppConfig.CountAsync(c => c.Key == ChapterFileDuplicateRepairService.MarkerKey));
    }
}
