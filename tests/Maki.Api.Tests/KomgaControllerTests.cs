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
[Collection(ConfigDirCollection.Name)]
public sealed class KomgaControllerTests : IDisposable
{
    private const int Pages = 4;
    private readonly TestDb _db = new();
    private readonly ReadingProgressGate _gate = new();
    private readonly IDataProtectionProvider _protection = new EphemeralDataProtectionProvider();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maki-komga-tests", Guid.NewGuid().ToString("N"));
    private readonly string _configDir;
    private readonly string? _priorEnv;
    private readonly AppPaths _paths;

    public KomgaControllerTests()
    {
        Directory.CreateDirectory(_root);
        _configDir = Path.Combine(_root, "config");
        _priorEnv = Environment.GetEnvironmentVariable("MAKI_CONFIG_DIR");
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _configDir);
        _paths = new AppPaths();
    }

    public void Dispose()
    {
        _db.Dispose();
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _priorEnv);
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
        var libraries = Assert.IsType<List<KomgaLibrary>>(Ok(await controller.Libraries(default)));
        Assert.Equal([RootFolderOf(granted.SeriesId).ToString()], libraries.Select(l => l.Id));
        Assert.IsType<NotFoundResult>(await controller.OneSeries(hidden.SeriesId.ToString(), default));
        Assert.IsType<NotFoundResult>(await controller.Pages(hidden.Chapters[0].ToString(), default));
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

    private static object? Ok(IActionResult result) => Assert.IsType<OkObjectResult>(result).Value;

    private (int UserId, string Token) SeedReader(bool opds = true, bool komga = true, bool allRootFolders = true)
    {
        var userId = _db.SeedUser("reader", MakiPermission.UseOpds, allRootFolders);
        _db.SetUserConfig(userId,
            (SettingKeys.OpdsEnabled, opds ? "true" : "false"),
            (SettingKeys.KomgaEnabled, komga ? "true" : "false"));
        return (userId, _db.SeedApiKey(userId, UserApiKeyScope.Opds));
    }

    private (int SeriesId, List<int> Chapters) SeedSeries(string title, params decimal[] numbers)
    {
        var seriesId = _db.SeedSeries(title);
        var folder = Path.Combine(_root, title);
        Directory.CreateDirectory(folder);

        using var db = _db.NewContext();
        var series = db.Series.Include(s => s.RootFolder).Single(s => s.Id == seriesId);
        series.RootFolder!.Path = folder;
        series.FolderName = "";
        var chapters = new List<int>();
        foreach (var number in numbers)
        {
            var path = Path.Combine(folder, $"{number}.cbz");
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
            var chapter = new Chapter { SeriesId = seriesId, Number = number, Language = "en", ChapterFileId = file.Id };
            db.Chapters.Add(chapter);
            db.SaveChanges();
            chapters.Add(chapter.Id);
        }

        return (seriesId, chapters);
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
