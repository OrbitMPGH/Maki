using Maki.Api.Configuration;
using Maki.Api.Controllers;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// The reader's page endpoint against a PDF chapter file. It must resolve pages through the same
/// synthetic <c>NNNN.jpg</c> names <see cref="PdfReader.PageName"/> produces rather than a zip
/// entry, and 404 past the page count exactly like a CBZ would past its own entry list.
/// </summary>
[Collection(ConfigDirCollection.Name)]
public sealed class ReaderPdfPageTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _configDir;
    private readonly string? _priorEnv;
    private readonly AppPaths _paths;
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "maki-reader-pdf-" + Guid.NewGuid().ToString("N")[..8]);

    public ReaderPdfPageTests()
    {
        Directory.CreateDirectory(_root);
        _configDir = Path.Combine(Path.GetTempPath(), "maki-reader-pdf-config", Guid.NewGuid().ToString("N"));
        _priorEnv = Environment.GetEnvironmentVariable("MAKI_CONFIG_DIR");
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _configDir);
        _paths = new AppPaths();
    }

    public void Dispose()
    {
        _db.Dispose();
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _priorEnv);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_configDir)) Directory.Delete(_configDir, recursive: true);
    }

    private int _chapterFileId;

    /// <summary>A three-page PDF, seeded as a series with one chapter backed by it.</summary>
    private int SeedPdfChapter()
    {
        using var db = _db.NewContext();
        var root = new RootFolder { Path = _root };
        db.RootFolders.Add(root);
        db.SaveChanges();

        var series = new Series { Title = "Look Back", SortTitle = "look back", FolderName = "Look Back", RootFolderId = root.Id };
        db.Series.Add(series);
        db.SaveChanges();

        var pdfPath = Path.Combine(_root, "Look Back.pdf");
        PdfFixture.Write(pdfPath, count: 3);

        var file = new ChapterFile
        {
            SeriesId = series.Id,
            RelativePath = "Look Back.pdf",
            Size = new FileInfo(pdfPath).Length,
            SourceName = "test",
            DateAdded = DateTime.UtcNow
        };
        db.ChapterFiles.Add(file);
        db.SaveChanges();
        _chapterFileId = file.Id;

        var chapter = new Chapter { SeriesId = series.Id, Number = 1, IsOneShot = true, ChapterFileId = file.Id };
        db.Chapters.Add(chapter);
        db.SaveChanges();
        return chapter.Id;
    }

    private ReaderController Controller(Maki.Data.MakiDbContext db) => new(
        new TestLocalizer(),
        db,
        new ReaderService(db, new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance), null!, null!, NullLogger<ReaderService>.Instance),
        null!, // continue reading
        null!, // profiles
        null!, // read import
        null!, // metrics
        null!, // achievements
        _paths,
        NullLogger<ReaderController>.Instance)
    {
        ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext()
        }
    };

    [Fact]
    public async Task PageOneOfAPdfChapterRendersAsAnImage()
    {
        var chapterId = SeedPdfChapter();
        using var db = _db.NewContext();

        var result = await Controller(db).Page(chapterId, 0, CancellationToken.None);

        var file = Assert.IsAssignableFrom<FileResult>(result);
        Assert.Equal("image/jpeg", file.ContentType);
    }

    [Fact]
    public async Task APageIndexPastTheDocumentsPageCountIsNotFound()
    {
        var chapterId = SeedPdfChapter();
        using var db = _db.NewContext();

        // The PDF has 3 pages (indices 0-2); page 8 (the 0009.jpg the issue's test plan names)
        // does not exist.
        var result = await Controller(db).Page(chapterId, 8, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task APageRequestCachesTheFullRenderOnDisk()
    {
        var chapterId = SeedPdfChapter();
        using var db = _db.NewContext();

        await Controller(db).Page(chapterId, 0, CancellationToken.None);

        var dir = Path.Combine(_paths.ReaderCacheDir, _chapterFileId.ToString());
        var cached = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.full.jpg") : [];
        Assert.Single(cached);
    }

    [Fact]
    public async Task AThumbnailRequestAfterAPageRequestDoesNotTouchTheCachedFullRender()
    {
        var chapterId = SeedPdfChapter();
        using var db = _db.NewContext();
        var controller = Controller(db);

        await controller.Page(chapterId, 0, CancellationToken.None);
        var dir = Path.Combine(_paths.ReaderCacheDir, _chapterFileId.ToString());
        var cached = Directory.GetFiles(dir, "*.full.jpg").Single();
        var before = new FileInfo(cached);
        var beforeWrite = before.LastWriteTimeUtc;
        var beforeLength = before.Length;

        await controller.Thumbnail(chapterId, 0, CancellationToken.None);

        var after = new FileInfo(cached);
        Assert.Equal(beforeWrite, after.LastWriteTimeUtc);
        Assert.Equal(beforeLength, after.Length);
    }

    [Fact]
    public async Task ConcurrentRequestsForTheSamePageAllSucceed()
    {
        var chapterId = SeedPdfChapter();

        // Each concurrent request gets its own DbContext, the way separate HTTP requests would -
        // an EF context is not safe to share across threads.
        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(async _ =>
            {
                using var db = _db.NewContext();
                return await Controller(db).Page(chapterId, 0, CancellationToken.None);
            }));

        foreach (var result in results)
        {
            var file = Assert.IsAssignableFrom<FileResult>(result);
            Assert.Equal("image/jpeg", file.ContentType);
        }
    }
}
