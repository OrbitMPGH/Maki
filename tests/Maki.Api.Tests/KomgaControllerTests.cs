using System.IO.Compression;
using System.Text;
using Maki.Api.Configuration;
using Maki.Api.Controllers;
using Maki.Api.Komga;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// The Komga-compatible API: which credentials open it, that it serves only what the token's owner
/// can see, and that progress from a Komga client lands in Maki's own progress rows.
/// </summary>
public sealed class KomgaControllerTests : IDisposable
{
    private const int Pages = 4;
    private readonly TestDb _db = new();
    private readonly ReadingProgressGate _gate = new();
    private readonly IDataProtectionProvider _protection = new EphemeralDataProtectionProvider();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maki-komga-tests", Guid.NewGuid().ToString("N"));
    private readonly string _configDir;
    private readonly AppPaths _paths;

    public KomgaControllerTests()
    {
        Directory.CreateDirectory(_root);
        _configDir = Path.Combine(_root, "config");
        _paths = new AppPaths(_configDir);
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task The_opds_token_opens_the_api_as_an_api_key_header()
    {
        var (_, token) = SeedReader();

        var http = Request(r => r.Headers[KomgaAuthenticator.ApiKeyHeader] = token);

        Assert.NotNull(await Authenticator().AuthenticateAsync(http, default));
        Assert.Contains(KomgaAuthenticator.CookieName, http.Response.Headers.SetCookie.ToString());
    }

    [Fact]
    public async Task The_opds_token_opens_the_api_as_a_basic_password()
    {
        var (_, token) = SeedReader();

        var http = Request(r => r.Headers.Authorization =
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"anyone:{token}")));

        Assert.NotNull(await Authenticator().AuthenticateAsync(http, default));
    }

    [Fact]
    public async Task The_session_cookie_authenticates_reads_and_puts_but_never_a_post()
    {
        var (_, token) = SeedReader();
        var first = Request(r => r.Headers[KomgaAuthenticator.ApiKeyHeader] = token);
        await Authenticator().AuthenticateAsync(first, default);
        var cookie = first.Response.Headers.SetCookie.ToString().Split(';')[0];

        Assert.NotNull(await Authenticator().AuthenticateAsync(Request(r => r.Headers.Cookie = cookie), default));
        Assert.NotNull(await Authenticator().AuthenticateAsync(
            Request(r => { r.Method = "PUT"; r.Headers.Cookie = cookie; }), default));
        Assert.Null(await Authenticator().AuthenticateAsync(
            Request(r => { r.Method = "POST"; r.Headers.Cookie = cookie; }), default));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Both_switches_have_to_be_on(bool opds, bool komga)
    {
        var (_, token) = SeedReader(opds, komga);

        var http = Request(r => r.Headers[KomgaAuthenticator.ApiKeyHeader] = token);

        Assert.Null(await Authenticator().AuthenticateAsync(http, default));
    }

    [Fact]
    public async Task A_full_api_key_does_not_open_it()
    {
        var (userId, _) = SeedReader();
        var full = _db.SeedApiKey(userId);

        var http = Request(r => r.Headers[KomgaAuthenticator.ApiKeyHeader] = full);

        Assert.Null(await Authenticator().AuthenticateAsync(http, default));
    }

    [Fact]
    public async Task Series_and_libraries_are_limited_to_the_granted_root_folders()
    {
        var granted = SeedSeries("Granted", 1m);
        var hidden = SeedSeries("Hidden", 1m);
        var (userId, _) = SeedReader(allRootFolders: false);
        using (var db = _db.NewContext())
        {
            db.UserRootFolders.Add(new UserRootFolder { UserId = userId, RootFolderId = RootFolderOf(granted.SeriesId) });
            db.SaveChanges();
        }

        var controller = Controller(userId, allRootFolders: false);

        var series = Assert.IsType<KomgaPage<KomgaSeries>>(Ok(await controller.Series(
            null, null, null, null, null, null, null, null, null)));
        Assert.Equal([granted.SeriesId.ToString()], series.Content.Select(s => s.Id));
        foreach (var sort in new[] { "lastModifiedDate,desc", "booksCount,asc", "createdDate,desc" })
        {
            var sorted = Assert.IsType<KomgaPage<KomgaSeries>>(Ok(await controller.Series(
                null, null, null, null, null, null, null, null, [sort])));
            Assert.Equal(1, sorted.TotalElements);
        }

        var libraries = Assert.IsType<List<KomgaLibrary>>(Ok(await controller.Libraries(default)));
        Assert.Equal([RootFolderOf(granted.SeriesId).ToString()], libraries.Select(l => l.Id));
        Assert.IsType<NotFoundResult>(await controller.OneSeries(hidden.SeriesId.ToString(), default));
        Assert.IsType<NotFoundResult>(await controller.Pages(hidden.Chapters[0].ToString(), default));
    }

    [Fact]
    public async Task An_archived_series_and_its_books_answer_404_everywhere()
    {
        var seeded = SeedSeries("Shelved", 1m, 2m);
        var kept = SeedSeries("Kept", 1m);
        var (userId, _) = SeedReader();
        using (var db = _db.NewContext())
        {
            db.Series.Single(s => s.Id == seeded.SeriesId).ArchivedAt = DateTime.UtcNow;
            db.SaveChanges();
        }

        var controller = Controller(userId);
        var series = seeded.SeriesId.ToString();
        var book = seeded.Chapters[0].ToString();

        Assert.IsType<NotFoundResult>(await controller.OneSeries(series, default));
        Assert.IsType<NotFoundResult>(await controller.SeriesThumbnail(series, default));
        Assert.IsType<NotFoundResult>(await controller.SeriesBooks(series, null, null, unpaged: true));
        Assert.IsType<NotFoundResult>(await controller.MarkSeriesRead(series, default));
        Assert.IsType<NotFoundResult>(await controller.TachiyomiProgressV2(series, default));
        Assert.IsType<NotFoundResult>(await controller.SetTachiyomiProgressV2(
            series, new KomgaTachiyomiUpdateV2(1), default));
        Assert.IsType<NotFoundResult>(await controller.Book(book, default));
        Assert.IsType<NotFoundResult>(await controller.NextBook(book, default));
        Assert.IsType<NotFoundResult>(await controller.BookThumbnail(book, default));
        Assert.IsType<NotFoundResult>(await controller.Download(book, default));
        Assert.IsType<NotFoundResult>(await controller.Pages(book, default));
        Assert.IsType<NotFoundResult>(await controller.Page(book, 1, null, default));
        Assert.IsType<NotFoundResult>(await controller.SetBookProgress(book, new KomgaReadProgressUpdate(2, null), default));
        Assert.IsType<NotFoundResult>(await controller.ClearBookProgress(book, default));
        Assert.Null(Progress(seeded.Chapters[0]));
        Assert.IsAssignableFrom<FileResult>(await controller.Page(kept.Chapters[0].ToString(), 1, null, default));
    }

    [Fact]
    public async Task A_page_number_far_past_the_end_is_an_empty_page()
    {
        var seeded = SeedSeries("Far", 1m);
        var (userId, _) = SeedReader();
        var controller = Controller(userId);

        var series = Assert.IsType<KomgaPage<KomgaSeries>>(Ok(await controller.Series(
            null, null, null, null, null, null, null, null, null, page: 2_200_000, size: 1000)));
        var books = Assert.IsType<KomgaPage<KomgaBook>>(Ok(await controller.Books(
            null, null, null, null, page: 2_200_000, size: 1000)));
        var seriesBooks = Assert.IsType<KomgaPage<KomgaBook>>(Ok(await controller.SeriesBooks(
            seeded.SeriesId.ToString(), null, null, page: 2_200_000, size: 1000)));

        Assert.Empty(series.Content);
        Assert.Empty(books.Content);
        Assert.Empty(seriesBooks.Content);
    }

    [Fact]
    public async Task Books_come_in_reading_order_with_their_number_and_page_count()
    {
        var seeded = SeedSeries("Ordered", 2m, 1m, 1.5m);
        var (userId, _) = SeedReader();

        var page = Assert.IsType<KomgaPage<KomgaBook>>(Ok(await Controller(userId).SeriesBooks(
            seeded.SeriesId.ToString(), null, null, unpaged: true)));

        Assert.Equal([1d, 1.5d, 2d], page.Content.Select(b => b.Metadata.NumberSort));
        Assert.Equal([1, 2, 3], page.Content.Select(b => b.Number));
        Assert.All(page.Content, b => Assert.Equal(Pages, b.Media.PagesCount));
        Assert.True(page.Last);
    }

    [Fact]
    public async Task Pages_are_one_based_and_fetching_one_records_nothing()
    {
        var seeded = SeedSeries("Paged", 1m);
        var (userId, _) = SeedReader();
        var controller = Controller(userId);
        var chapter = seeded.Chapters[0].ToString();

        var pages = Assert.IsType<List<KomgaPageInfo>>(Ok(await controller.Pages(chapter, default)));
        Assert.Equal([1, 2, 3, 4], pages.Select(p => p.Number));
        Assert.IsAssignableFrom<FileResult>(await controller.Page(chapter, Pages, default));
        Assert.IsType<NotFoundResult>(await controller.Page(chapter, 0, default));
        Assert.Null(Progress(seeded.Chapters[0]));
    }

    private static byte[] Avif() => Solid().HeifsaveBuffer(q: 60, compression: NetVips.Enums.ForeignHeifCompression.Av1);

    private static byte[] Webp() => Solid().WebpsaveBuffer(q: 80);

    private static NetVips.Image Solid() => (NetVips.Image.Black(64, 48, bands: 3) + new[] { 40.0, 120.0, 200.0 }).Cast(NetVips.Enums.BandFormat.Uchar);

    private static (int Width, int Height, Maki.Core.Images.ImageFormat? Format) Header(byte[] bytes)
    {
        var header = Maki.Core.Images.ImageCodec.Identify(bytes);
        return (header.Width, header.Height, header.Format);
    }

    [Fact]
    public async Task A_page_a_client_cannot_decode_is_converted_on_request()
    {
        var jpeg = new Maki.Core.Images.Raster(64, 48, bands: 3).EncodeJpeg(80);
        var seeded = SeedImageSeries("Converted", ("001.avif", Avif()), ("002.webp", Webp()), ("003.jpg", jpeg));
        var (userId, _) = SeedReader();
        var controller = Controller(userId);
        var book = seeded.ChapterId.ToString();

        var pages = Assert.IsType<List<KomgaPageInfo>>(Ok(await controller.Pages(book, default)));
        Assert.Equal(["image/avif", "image/webp", "image/jpeg"], pages.Select(p => p.MediaType));

        var asIs = Assert.IsType<FileStreamResult>(await controller.Page(book, 1, null, default));
        Assert.Equal("image/avif", asIs.ContentType);

        var avifAsJpeg = Assert.IsType<FileContentResult>(await controller.Page(book, 1, "jpeg", default));
        Assert.Equal("image/jpeg", avifAsJpeg.ContentType);
        Assert.Equal((64, 48, Maki.Core.Images.ImageFormat.Jpeg), Header(avifAsJpeg.FileContents));
        Assert.EndsWith("-jpeg\"", avifAsJpeg.EntityTag!.Tag.ToString());

        var webpAsPng = Assert.IsType<FileContentResult>(await controller.Page(book, 2, "PNG", default));
        Assert.Equal("image/png", webpAsPng.ContentType);
        Assert.Equal((64, 48, Maki.Core.Images.ImageFormat.Png), Header(webpAsPng.FileContents));

        var jpegAsJpeg = Assert.IsType<FileContentResult>(await controller.Page(book, 3, "jpeg", default));
        Assert.Equal(jpeg, jpegAsJpeg.FileContents);
    }

    [Fact]
    public async Task An_unknown_conversion_is_refused_and_one_maki_cannot_decode_is_a_415()
    {
        var seeded = SeedSeries("Unconvertible", 1m);
        var (userId, _) = SeedReader();
        var controller = Controller(userId);
        var book = seeded.Chapters[0].ToString();

        Assert.IsType<BadRequestResult>(await controller.Page(book, 1, "gif", default));
        var refused = Assert.IsType<StatusCodeResult>(await controller.Page(book, 1, "png", default));
        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, refused.StatusCode);
    }

    [Fact]
    public async Task A_book_progress_patch_is_saved_through_the_reader()
    {
        var seeded = SeedSeries("Patched", 1m, 2m);
        var (userId, _) = SeedReader();
        var controller = Controller(userId);

        await controller.SetBookProgress(seeded.Chapters[0].ToString(), new KomgaReadProgressUpdate(2, null), default);
        await controller.SetBookProgress(seeded.Chapters[1].ToString(), new KomgaReadProgressUpdate(null, true), default);

        Assert.Equal(1, Progress(seeded.Chapters[0])!.PageIndex);
        Assert.False(Progress(seeded.Chapters[0])!.Completed);
        Assert.True(Progress(seeded.Chapters[1])!.Completed);
        var book = Assert.IsType<KomgaBook>(Ok(await Controller(userId).Book(seeded.Chapters[0].ToString(), default)));
        Assert.Equal(2, book.ReadProgress!.Page);

        await Controller(userId).ClearBookProgress(seeded.Chapters[1].ToString(), default);
        Assert.False(Progress(seeded.Chapters[1])!.Completed);
    }

    [Fact]
    public async Task One_step_from_the_tracker_is_a_read_and_a_jump_is_a_silent_mark()
    {
        var seeded = SeedSeries("Tracked", 1m, 2m, 3m, 4m);
        var (userId, _) = SeedReader();

        await Controller(userId).SetTachiyomiProgressV2(
            seeded.SeriesId.ToString(), new KomgaTachiyomiUpdateV2(1), default);
        Assert.True(Progress(seeded.Chapters[0])!.Completed);
        Assert.False(Progress(seeded.Chapters[0])!.BulkMarked);

        await Controller(userId).SetTachiyomiProgressV2(
            seeded.SeriesId.ToString(), new KomgaTachiyomiUpdateV2(3), default);
        Assert.True(Progress(seeded.Chapters[2])!.Completed);
        Assert.True(Progress(seeded.Chapters[2])!.BulkMarked);
        Assert.Null(Progress(seeded.Chapters[3]));

        var progress = Assert.IsType<KomgaTachiyomiProgressV2>(Ok(await Controller(userId).TachiyomiProgressV2(
            seeded.SeriesId.ToString(), default)));
        Assert.Equal(3, progress.LastReadContinuousNumberSort);
        Assert.Equal(4, progress.MaxNumberSort);
        Assert.Equal(3, progress.BooksReadCount);
        Assert.Equal(1, progress.BooksUnreadCount);
    }

    [Fact]
    public async Task A_v1_tracker_update_counts_books_from_the_start()
    {
        var seeded = SeedSeries("Counted", 1m, 2m, 3m);
        var (userId, _) = SeedReader();

        await Controller(userId).SetTachiyomiProgress(seeded.SeriesId.ToString(), new KomgaTachiyomiUpdate(2), default);

        Assert.True(Progress(seeded.Chapters[1])!.Completed);
        Assert.Null(Progress(seeded.Chapters[2]));
        var progress = Assert.IsType<KomgaTachiyomiProgress>(Ok(await Controller(userId).TachiyomiProgress(
            seeded.SeriesId.ToString(), default)));
        Assert.Equal(2, progress.LastReadContinuousIndex);
    }

    [Fact]
    public async Task The_tracker_counts_a_chapter_read_in_one_language_as_read()
    {
        var seeded = SeedSeries("Bilingual", ["en", "es"], 1m, 2m, 3m, 4m);
        var (userId, _) = SeedReader();

        await Controller(userId).SetTachiyomiProgress(seeded.SeriesId.ToString(), new KomgaTachiyomiUpdate(4), default);

        // en copies are the first four ids; lastBookRead counts chapters, not language copies.
        Assert.True(Progress(seeded.Chapters[3])!.Completed);
        var v1 = Assert.IsType<KomgaTachiyomiProgress>(Ok(await Controller(userId).TachiyomiProgress(
            seeded.SeriesId.ToString(), default)));
        Assert.Equal(4, v1.BooksCount);
        Assert.Equal(4, v1.BooksReadCount);
        Assert.Equal(4, v1.LastReadContinuousIndex);
    }

    private static object? Ok(IActionResult result) => Assert.IsType<OkObjectResult>(result).Value;

    private (int UserId, string Token) SeedReader(bool opds = true, bool komga = true, bool allRootFolders = true)
    {
        var userId = _db.SeedUser("reader", MakiPermission.UseOpds, allRootFolders);
        _db.SetUserConfig(userId,
            (SettingKeys.OpdsEnabled, opds ? "true" : "false"),
            (SettingKeys.KomgaEnabled, komga ? "true" : "false"));
        return (userId, _db.SeedApiKey(userId, UserApiKeyScope.Opds));
    }

    private (int SeriesId, List<int> Chapters) SeedSeries(string title, params decimal[] numbers) =>
        SeedSeries(title, ["en"], numbers);

    /// <summary>A series with a copy of every chapter in each language; chapter ids come language by language.</summary>
    private (int SeriesId, List<int> Chapters) SeedSeries(string title, string[] languages, params decimal[] numbers)
    {
        var seriesId = _db.SeedSeries(title);
        var folder = Path.Combine(_root, title);
        Directory.CreateDirectory(folder);

        using var db = _db.NewContext();
        var series = db.Series.Include(s => s.RootFolder).Single(s => s.Id == seriesId);
        series.RootFolder!.Path = folder;
        series.FolderName = "";
        var chapters = new List<int>();
        foreach (var (language, number) in languages.SelectMany(l => numbers.Select(n => (l, n))))
        {
            var path = Path.Combine(folder, $"{number}-{language}.cbz");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                for (var i = 1; i <= Pages; i++)
                {
                    using var stream = archive.CreateEntry($"{i:000}.jpg").Open();
                    stream.WriteByte(0xFF);
                }
            }

            var file = new ChapterFile
            {
                SeriesId = seriesId,
                RelativePath = Path.GetFileName(path),
                Size = new FileInfo(path).Length,
                SourceName = "Test",
                DateAdded = DateTime.UtcNow,
            };
            db.ChapterFiles.Add(file);
            db.SaveChanges();
            var chapter = new Chapter { SeriesId = seriesId, Number = number, Language = language, ChapterFileId = file.Id };
            db.Chapters.Add(chapter);
            db.SaveChanges();
            chapters.Add(chapter.Id);
        }

        return (seriesId, chapters);
    }

    /// <summary>One chapter whose archive holds exactly these pages.</summary>
    private (int SeriesId, int ChapterId) SeedImageSeries(string title, params (string Name, byte[] Bytes)[] pages)
    {
        var seriesId = _db.SeedSeries(title);
        var folder = Path.Combine(_root, title);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "1.cbz");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var (name, bytes) in pages)
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(bytes);
            }
        }

        using var db = _db.NewContext();
        var series = db.Series.Include(s => s.RootFolder).Single(s => s.Id == seriesId);
        series.RootFolder!.Path = folder;
        series.FolderName = "";
        var file = new ChapterFile
        {
            SeriesId = seriesId, RelativePath = "1.cbz", Size = new FileInfo(path).Length, SourceName = "Test",
            DateAdded = DateTime.UtcNow,
        };
        db.ChapterFiles.Add(file);
        db.SaveChanges();
        var chapter = new Chapter { SeriesId = seriesId, Number = 1, Language = "en", ChapterFileId = file.Id };
        db.Chapters.Add(chapter);
        db.SaveChanges();
        return (seriesId, chapter.Id);
    }

    private int RootFolderOf(int seriesId)
    {
        using var db = _db.NewContext();
        return db.Series.Single(s => s.Id == seriesId).RootFolderId;
    }

    private ChapterProgress? Progress(int chapterId)
    {
        using var db = _db.NewContext();
        return db.ChapterProgress.IgnoreQueryFilters().AsNoTracking().SingleOrDefault(p => p.ChapterId == chapterId);
    }

    private static DefaultHttpContext Request(Action<HttpRequest> configure)
    {
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        configure(http.Request);
        return http;
    }

    private KomgaAuthenticator Authenticator()
    {
        var nobody = new DataScope();
        nobody.SetNobody();
        return new KomgaAuthenticator(new OpdsAccessService(new MakiDbContext(_db.Options, nobody), TimeProvider.System),
            _protection);
    }

    /// <summary>A controller as <see cref="KomgaAuthFilter"/> leaves it: the scope narrowed to the token's owner.</summary>
    private KomgaController Controller(int userId, bool allRootFolders = true)
    {
        var db = _db.NewContext(userId, allRootFolders);
        var reader = new ReaderService(db, new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance),
            new ReadingProgressService(db, _gate, NullLogger<ReadingProgressService>.Instance),
            InertKavitaPusher.For(_db.ScopeFactory()), new ReadingSessionService(db), NullLogger<ReaderService>.Instance);
        var catalog = new KomgaCatalogService(db, reader, new ContinueReadingService(db));
        var http = new DefaultHttpContext();
        http.Items[typeof(OpdsAccess)] = new OpdsAccess(true, userId, allRootFolders, Komga: true);
        return new KomgaController(catalog, reader, db, _paths)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }
}
