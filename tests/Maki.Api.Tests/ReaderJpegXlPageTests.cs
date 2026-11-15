using System.IO.Compression;
using Maki.Api.Configuration;
using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Images;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// The reader's page endpoint for a chapter whose pages page conversion stored as JPEG XL: sent as it is
/// to a browser that says it decodes JPEG XL, as WebP to one that does not.
/// </summary>
public sealed class ReaderJpegXlPageTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "maki-reader-jxl-config", Guid.NewGuid().ToString("N"));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maki-reader-jxl-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly AppPaths _paths;

    public ReaderJpegXlPageTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new AppPaths(_configDir);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_configDir)) Directory.Delete(_configDir, recursive: true);
    }

    private static bool Skipped => !JpegXl.IsAvailable && Environment.GetEnvironmentVariable("MAKI_REQUIRE_LIBJXL") != "1";

    internal static byte[] JxlPage(int width = 120, int height = 180)
    {
        var rgb = new byte[width * height * 3];
        for (var i = 0; i < rgb.Length; i++) rgb[i] = (byte)(i * 7);
        return JpegXl.Encode(rgb, width, height, 3, PageConversionSettings.Distance(PageConversionStop.High), ImageCodec.PageJpegXlEffort);
    }

    private int SeedChapter(params (string Name, byte[] Bytes)[] pages)
    {
        var cbz = Path.Combine(_root, "Chapter.cbz");
        using (var archive = ZipFile.Open(cbz, ZipArchiveMode.Create))
        {
            foreach (var (name, bytes) in pages)
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(bytes);
            }
        }

        using var db = _db.NewContext();
        var root = new RootFolder { Path = _root };
        db.RootFolders.Add(root);
        db.SaveChanges();
        var series = new Series { Title = "Jxl", SortTitle = "jxl", FolderName = "", RootFolderId = root.Id };
        db.Series.Add(series);
        db.SaveChanges();
        var file = new ChapterFile
        {
            SeriesId = series.Id, RelativePath = "Chapter.cbz", Size = new FileInfo(cbz).Length, SourceName = "test",
            DateAdded = DateTime.UtcNow, ImageFormat = "jxl"
        };
        db.ChapterFiles.Add(file);
        db.SaveChanges();
        var chapter = new Chapter { SeriesId = series.Id, Number = 1, Language = "en", ChapterFileId = file.Id };
        db.Chapters.Add(chapter);
        db.SaveChanges();
        return chapter.Id;
    }

    private static ReaderService Reader(Maki.Data.MakiDbContext db) =>
        new(db, new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance), null!, null!,
            new ReadingSessionService(db), NullLogger<ReaderService>.Instance);

    private ReaderController Controller(Maki.Data.MakiDbContext db, Action<HttpRequest>? request = null)
    {
        var http = new DefaultHttpContext();
        request?.Invoke(http.Request);
        return new ReaderController(new TestLocalizer(), db, Reader(db),
            new ReaderThumbnailCache(_paths, Reader(db), NullLogger<ReaderThumbnailCache>.Instance),
            null!, null!, null!, null!, _paths, null!, null!, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    [Fact]
    public async Task A_jpeg_xl_page_goes_out_as_it_is_to_a_browser_that_accepts_it()
    {
        if (Skipped) return;
        var jxl = JxlPage();
        var chapterId = SeedChapter(("001.jxl", jxl));
        using var db = _db.NewContext();

        var accepted = Controller(db, r => r.Headers.Accept = "image/jxl,image/avif,image/webp,*/*;q=0.8");
        var direct = Assert.IsType<FileStreamResult>(await accepted.Page(chapterId, 0, default));
        Assert.Equal("image/jxl", direct.ContentType);
        Assert.EndsWith("-0-jxl\"", direct.EntityTag!.Tag.ToString());
        Assert.Equal("Accept", accepted.Response.Headers.Vary.ToString());

        var flagged = Controller(db, r => r.QueryString = new QueryString("?jxl=1"));
        Assert.Equal("image/jxl", Assert.IsType<FileStreamResult>(await flagged.Page(chapterId, 0, default)).ContentType);
    }

    [Fact]
    public async Task A_browser_without_jpeg_xl_gets_webp_under_its_own_etag()
    {
        if (Skipped) return;
        var chapterId = SeedChapter(("001.jxl", JxlPage()));
        using var db = _db.NewContext();

        var controller = Controller(db, r => r.Headers.Accept = "image/avif,image/webp,image/*,*/*;q=0.8");
        var converted = Assert.IsType<FileContentResult>(await controller.Page(chapterId, 0, default));

        Assert.Equal("image/webp", converted.ContentType);
        Assert.Equal(new ImageHeader(120, 180, ImageFormat.Webp), ImageCodec.Identify(converted.FileContents));
        Assert.EndsWith("-0-webp\"", converted.EntityTag!.Tag.ToString());
        Assert.Equal("Accept", controller.Response.Headers.Vary.ToString());

        var refused = Controller(db, r => r.Headers.Accept = "image/jxl;q=0, image/webp");
        Assert.Equal("image/webp", Assert.IsType<FileContentResult>(await refused.Page(chapterId, 0, default)).ContentType);
    }

    [Fact]
    public async Task Other_pages_keep_their_type_their_etag_and_no_vary()
    {
        var jpeg = new Raster(64, 48, bands: 3).EncodeJpeg(80);
        var chapterId = SeedChapter(("001.jpg", jpeg));
        using var db = _db.NewContext();

        var controller = Controller(db);
        var page = Assert.IsType<FileStreamResult>(await controller.Page(chapterId, 0, default));

        Assert.Equal("image/jpeg", page.ContentType);
        Assert.EndsWith("-0\"", page.EntityTag!.Tag.ToString());
        Assert.Equal("", controller.Response.Headers.Vary.ToString());
    }

    [Fact]
    public async Task A_page_that_does_not_decode_is_sent_as_stored()
    {
        byte[] broken = [0xFF, 0x0A, 1, 2, 3, 4];
        var (bytes, type) = await JpegXlPages.TranscodeAsync(new MemoryStream(broken), default);

        Assert.Equal(broken, bytes);
        Assert.Equal("image/jxl", type);
    }

    [Fact]
    public async Task A_page_that_does_not_convert_is_never_cached_under_the_webp_etag()
    {
        var chapterId = SeedChapter(("001.jxl", [0xFF, 0x0A, 1, 2, 3, 4]));
        using var db = _db.NewContext();

        var controller = Controller(db, r =>
        {
            r.Headers.Accept = "image/webp,*/*;q=0.8";
            r.QueryString = new QueryString("?v=ignored");
        });
        var sent = Assert.IsType<FileContentResult>(await controller.Page(chapterId, 0, default));

        Assert.Equal("image/jxl", sent.ContentType);
        Assert.Null(sent.EntityTag);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public void Representation_follows_the_entry_and_what_the_client_takes()
    {
        Assert.Equal(("image/jxl", "-jxl"), JpegXlPages.Representation("001.jxl", true));
        Assert.Equal(("image/webp", "-webp"), JpegXlPages.Representation("001.JXL", false));
        Assert.Equal(("image/avif", ""), JpegXlPages.Representation("001.avif", false));
    }
}
