using System.IO.Compression;
using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Images;
using Maki.Core.Reading;
using Maki.Core.Tests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Tests;

public sealed partial class PageConversionTests
{
    private static readonly PageConversionSettings WebpSplit = Webp with { SplitHeight = 5000 };

    /// <summary>
    /// A chapter of three pages: an ordinary page, an 800 x 12000 strip with gutters at 4300 and 8900
    /// (three pieces once split at 5000) and another ordinary page.
    /// </summary>
    private (int ChapterId, int FileId, string Path) StripChapter(decimal number)
    {
        var (chapterId, fileId) = _world.Chapter(number, f => f.ImageFormat = "jpg");
        using var db = _world.Db.NewContext();
        var file = db.ChapterFiles.Single(f => f.Id == fileId);
        var path = Path.Combine(_world.Library, file.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var (name, bytes) in new[]
                     {
                         ("001.jpg", ScanJpeg(800, 1200, 1)),
                         ("002.jpg", TallStrips.Jpeg(800, 12000, [(4300, 4400), (8900, 9000)])),
                         ("003.jpg", ScanJpeg(800, 1200, 3))
                     })
            {
                using var stream = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                stream.Write(bytes);
            }
        }

        file.Size = new FileInfo(path).Length;
        file.PageCount = 3;
        file.MedianWidth = 800;
        file.MedianHeight = 1200;
        db.SaveChanges();
        return (chapterId, fileId!.Value, path);
    }

    private sealed record Marks(int Alice, int Bob, int ProgressA, int ProgressB, int Bookmark, int Note, int WholeNote);

    /// <summary>Alice halfway down the strip, Bob on the last page, a bookmark on the last page and a note on the strip.</summary>
    private Marks SeedMarks(int chapterId)
    {
        var alice = _world.Db.SeedUser("alice");
        var bob = _world.Db.SeedUser("bob");
        using var db = _world.Db.NewContext();
        var stamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var a = new ChapterProgress
        {
            UserId = alice, SeriesId = _world.SeriesId, ChapterId = chapterId, PageIndex = 1, PageOffset = 0.5, PageCount = 3,
            UpdatedAt = stamp
        };
        var b = new ChapterProgress
        {
            UserId = bob, SeriesId = _world.SeriesId, ChapterId = chapterId, PageIndex = 2, PageCount = 3, UpdatedAt = stamp
        };
        var bookmark = new ReaderBookmark { UserId = alice, SeriesId = _world.SeriesId, ChapterId = chapterId, PageIndex = 2 };
        var note = new ChapterNote { SeriesId = _world.SeriesId, ChapterId = chapterId, AuthorId = alice, PageIndex = 1, Body = "here" };
        var whole = new ChapterNote { SeriesId = _world.SeriesId, ChapterId = chapterId, AuthorId = bob, Body = "all of it" };
        db.AddRange(a, b, bookmark, note, whole);
        db.SaveChanges();
        db.ChapterProgress.Where(p => p.Id == a.Id || p.Id == b.Id)
            .ExecuteUpdate(set => set.SetProperty(p => p.UpdatedAt, stamp));
        return new Marks(alice, bob, a.Id, b.Id, bookmark.Id, note.Id, whole.Id);
    }

    private (ChapterProgress A, ChapterProgress B, int Bookmark, int? Note, int? WholeNote) Read(Marks marks)
    {
        using var db = _world.Db.NewContext();
        return (
            db.ChapterProgress.IgnoreQueryFilters().AsNoTracking().Single(p => p.Id == marks.ProgressA),
            db.ChapterProgress.IgnoreQueryFilters().AsNoTracking().Single(p => p.Id == marks.ProgressB),
            db.ReaderBookmarks.IgnoreQueryFilters().Single(b => b.Id == marks.Bookmark).PageIndex,
            db.ChapterNotes.IgnoreQueryFilters().Single(n => n.Id == marks.Note).PageIndex,
            db.ChapterNotes.IgnoreQueryFilters().Single(n => n.Id == marks.WholeNote).PageIndex);
    }

    private async Task<FileConversion> ConvertWithAsync(int fileId, PageConversionPlan plan)
    {
        using var db = _world.Db.NewContext();
        return await Service(db).ConvertAsync(fileId, plan, new PageConversionPassCache(), CancellationToken.None);
    }

    [Fact]
    public async Task A_split_moves_every_stored_page_index_with_its_page_and_an_undo_moves_it_back()
    {
        var (chapterId, fileId, path) = StripChapter(1);
        var original = File.ReadAllBytes(path);
        var marks = SeedMarks(chapterId);

        Assert.Equal(FileConversionStatus.Converted, (await ConvertWithAsync(fileId, PageConversionPlan.For(WebpSplit))).Status);

        using (var zip = ZipFile.OpenRead(path))
        {
            Assert.Equal(["001.webp", "002-01.webp", "002-02.webp", "002-03.webp", "003.webp"], CbzReader.PageNames(zip));
        }

        var (a, b, bookmark, note, whole) = Read(marks);
        // Halfway down the strip is the second of its three pieces, halfway down that piece.
        Assert.Equal((2, 5), (a.PageIndex, a.PageCount));
        Assert.Equal(0.5, a.PageOffset, 6);
        Assert.Equal((4, 5), (b.PageIndex, b.PageCount));
        Assert.Equal((4, 1, (int?)null), (bookmark, note, whole));
        // Moved without counting as a read or restamping the row.
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), a.UpdatedAt);

        var row = Row(fileId);
        Assert.Equal(("jpg", "webp:balanced+split5000"), (row.OriginalImageFormat, row.CheckedWith));
        Assert.Equal("1:3", History(fileId).SplitPages);
        // Readers count the split archive; upgrade scoring keeps the acquired count.
        Assert.Equal((5, 3), (row.SplitPageCount, row.PageCount));

        using (var db = _world.Db.NewContext())
        {
            var (undo, _, _) = Undo(db);
            var result = await undo.UndoAsync(_world.SeriesId, setNever: false, convertAgain: false, CancellationToken.None);
            Assert.Equal(1, result!.Restored);
        }

        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Null(Row(fileId).SplitPageCount);
        (a, b, bookmark, note, whole) = Read(marks);
        Assert.Equal((1, 3, 2, 3), (a.PageIndex, a.PageCount, b.PageIndex, b.PageCount));
        Assert.Equal(0.5, a.PageOffset, 6);
        Assert.Equal((2, 1, (int?)null), (bookmark, note, whole));
    }

    [Fact]
    public async Task Turning_the_split_on_later_splits_converted_files_and_undo_still_reaches_the_original()
    {
        var (chapterId, fileId, path) = StripChapter(1);
        var original = File.ReadAllBytes(path);
        var marks = SeedMarks(chapterId);
        Assert.Equal(FileConversionStatus.Converted, (await ConvertWithAsync(fileId, PageConversionPlan.For(Webp))).Status);
        var converted = Row(fileId);
        Assert.Null(History(fileId).SplitPages);

        var plan = PageConversionPlan.For(WebpSplit);
        using (var db = _world.Db.NewContext())
        {
            Assert.Equal(0, await new PageConversionPlanService(db).RecheckAsync(PageConversionPlan.For(Webp), null, CancellationToken.None));
            Assert.Equal(1, await new PageConversionPlanService(db).RecheckAsync(plan, null, CancellationToken.None));
        }

        Assert.Equal(FileConversionStatus.Converted, (await ConvertWithAsync(fileId, plan)).Status);

        var row = Row(fileId);
        Assert.Equal(("jpg", "split5000"), (row.OriginalImageFormat, row.CheckedWith));
        Assert.Equal(converted.ConversionSavedBytes + (converted.Size - row.Size), row.ConversionSavedBytes);
        Assert.Equal(converted.QualitySize, row.QualitySize);
        using (var db = _world.Db.NewContext())
        {
            var history = Assert.Single(db.PageConversionHistory.AsNoTracking().Where(h => h.ChapterFileId == fileId));
            Assert.Equal(("1:3", row.Size), (history.SplitPages, history.BytesAfter));
            Assert.Equal(0, await new PageConversionPlanService(db).RecheckAsync(plan, null, CancellationToken.None));
        }

        Assert.Equal(2, Read(marks).A.PageIndex);
        Assert.Single(TrashedOriginals());

        using (var db = _world.Db.NewContext())
        {
            var (undo, _, _) = Undo(db);
            Assert.Equal(1, (await undo.UndoAsync(_world.SeriesId, false, false, CancellationToken.None))!.Restored);
        }

        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal((1, 2), (Read(marks).A.PageIndex, Read(marks).Bookmark));
    }

    [Fact]
    public async Task A_file_with_no_tall_page_is_checked_and_left_alone_when_the_plan_only_splits()
    {
        var (_, fileId, path) = JpegChapter(1);
        using (var db = _world.Db.NewContext())
        {
            db.ChapterFiles.Where(f => f.Id == fileId).ExecuteUpdate(set => set.SetProperty(f => f.ImageFormat, "webp"));
        }

        var before = Hash(path);

        var result = await ConvertWithAsync(fileId, PageConversionPlan.For(WebpSplit));

        Assert.Equal(FileConversionStatus.Unchanged, result.Status);
        Assert.Equal(before, Hash(path));
        Assert.Equal("split5000", Row(fileId).CheckedWith);
    }

    [Fact]
    public async Task A_pass_waits_while_someone_reads_a_file_it_would_split()
    {
        var (_, fileId, path) = StripChapter(1);
        await using (await _world.Archives.OpenPageAsync(path, "001.jpg", CancellationToken.None)) { }

        Assert.Equal(FileConversionStatus.Busy, (await ConvertWithAsync(fileId, PageConversionPlan.For(WebpSplit))).Status);
        Assert.Equal(FileConversionStatus.Converted, (await ConvertWithAsync(fileId, PageConversionPlan.For(Webp))).Status);
    }

    [Fact]
    public async Task The_split_height_is_saved_with_the_settings_and_checked()
    {
        using var db = _world.Db.NewContext();
        var controller = Controller(db);

        var saved = Saved(await controller.Save(new PageConversionController.SettingsDto("webp", "high", false, 6000),
            CancellationToken.None));
        Assert.Equal(6000, saved.Settings.SplitHeight);
        Assert.Equal(6000, (await PageConversionOptions.LoadPlanAsync(db, CancellationToken.None))!.SplitHeight);

        Assert.IsType<BadRequestObjectResult>(await controller.Save(
            new PageConversionController.SettingsDto("webp", "high", false, 200), CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await controller.SplitPreview(50_000, null, null, CancellationToken.None));

        saved = Saved(await controller.Save(new PageConversionController.SettingsDto("webp", "high", false), CancellationToken.None));
        Assert.Null(saved.Settings.SplitHeight);
    }

    [Fact]
    public async Task The_split_preview_finds_the_tallest_page_and_where_it_would_be_cut()
    {
        var (_, fileId, _) = StripChapter(1);
        using var db = _world.Db.NewContext();
        db.ChapterFiles.Where(f => f.Id == fileId).ExecuteUpdate(set => set.SetProperty(f => f.MedianHeight, 1200));

        var result = Assert.IsType<OkObjectResult>(await Controller(db).SplitPreview(5000, null, null, CancellationToken.None));

        var preview = Assert.IsType<PageSplitPreview>(result.Value);
        Assert.Equal((fileId, 1, 3, "tall"), (preview.Sample.FileId, preview.Sample.Page, preview.Sample.PageCount, preview.Sample.Reason));
        Assert.Equal((800, 12000), (preview.Width, preview.Height));
        Assert.Equal([4350, 8950], preview.Cuts);

        var first = Assert.IsType<PageSplitPreview>(Assert.IsType<OkObjectResult>(
            await Controller(db).SplitPreview(5000, fileId, 0, CancellationToken.None)).Value);
        Assert.Equal((0, 1200), (first.Sample.Page, first.Height));
        Assert.Empty(first.Cuts);
    }
}
