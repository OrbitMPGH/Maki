using System.IO.Compression;
using System.Net;
using System.Reflection;
using Maki.Api.Auth;
using Maki.Api.Configuration;
using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Images;
using Maki.Core.Kavita;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// The poster picked from a chapter page, a linked source's cover or a URL: stored like an upload and
/// locked, with the URL fetch held to web addresses, the size cap and a real image.
/// </summary>
public class CoverPickTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _configDir;
    private readonly AppPaths _paths;
    private readonly ReadingProgressGate _gate = new();

    public CoverPickTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "maki-cover-pick-tests", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_configDir);
    }

    [Fact]
    public async Task A_url_image_is_stored_resized_and_locked()
    {
        var id = _db.SeedSeries("Berserk");
        var web = new Web { ["https://img.example.com/poster.png"] = Ok(Png(900, 1200)) };
        await using var db = _db.NewContext();

        var result = await Controller(db).FromUrl(id, new("https://img.example.com/poster.png"), Fetcher(web), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var series = await Reload(id);
        Assert.True(series.IsLocked(SeriesMetadataField.Cover));
        Assert.Equal(400, ImageCodec.IdentifyFile(series.CoverPath!).Width);
    }

    [Theory]
    [InlineData("ftp://img.example.com/poster.png")]
    [InlineData("file:///etc/passwd")]
    [InlineData("poster.png")]
    [InlineData(null)]
    public async Task A_url_that_is_not_a_web_address_is_refused_unfetched(string? url)
    {
        var id = _db.SeedSeries("Berserk");
        var web = new Web();
        await using var db = _db.NewContext();

        var result = await Controller(db).FromUrl(id, new(url), Fetcher(web), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(web.Requested);
        Assert.False((await Reload(id)).IsLocked(SeriesMetadataField.Cover));
    }

    [Fact]
    public async Task A_redirect_to_another_scheme_is_not_followed()
    {
        var id = _db.SeedSeries("Berserk");
        var web = new Web { ["https://img.example.com/poster.png"] = Redirect("file:///etc/passwd") };
        await using var db = _db.NewContext();

        var result = await Controller(db).FromUrl(id, new("https://img.example.com/poster.png"), Fetcher(web), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(["https://img.example.com/poster.png"], web.Requested);
    }

    [Fact]
    public async Task A_redirect_between_web_addresses_is_followed()
    {
        var id = _db.SeedSeries("Berserk");
        var web = new Web
        {
            ["http://img.example.com/poster.png"] = Redirect("https://cdn.example.net/p.png"),
            ["https://cdn.example.net/p.png"] = Ok(Png(10, 15)),
        };
        await using var db = _db.NewContext();

        var result = await Controller(db).FromUrl(id, new("http://img.example.com/poster.png"), Fetcher(web), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
    }

    // The body is built inside the test: xUnit serializes theory data at discovery, and a 10 MB array
    // there ran the CI runner out of memory.
    [Theory]
    [InlineData("html")]
    [InlineData("too large")]
    public async Task A_url_answer_that_is_not_a_usable_image_is_refused(string what)
    {
        var body = what == "html"
            ? "<html><script>alert(1)</script></html>"u8.ToArray()
            : new byte[MediaCoverController.CoverUploadLimit + 1];
        var id = _db.SeedSeries("Berserk");
        var web = new Web { ["https://img.example.com/poster.png"] = Ok(body) };
        await using var db = _db.NewContext();

        var result = await Controller(db).FromUrl(id, new("https://img.example.com/poster.png"), Fetcher(web), CancellationToken.None);

        Assert.True(result is BadRequestObjectResult, what);
        Assert.False(File.Exists(Covers().CoverPathFor(id)));
    }

    [Fact]
    public async Task A_source_cover_on_its_own_host_is_stored_with_its_referer()
    {
        var (id, mappingId) = SeedMapped("Berserk");
        var web = new Web { ["https://cdn.fake.test/b.png"] = Ok(Png(30, 45)) };
        var source = Source("https://cdn.fake.test/b.png");
        await using var db = _db.NewContext();

        var result = await Controller(db).FromSource(
            id, new(mappingId), Registry(source), Availability(source), Fetcher(web), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.True((await Reload(id)).IsLocked(SeriesMetadataField.Cover));
        Assert.Equal("https://fake.test/", web.Referers.Single());
    }

    [Theory]
    [InlineData("https://elsewhere.example/b.png", null)]
    [InlineData("https://cdn.fake.test/b.png", "http://169.254.169.254/latest")]
    public async Task A_source_cover_off_the_sources_hosts_is_refused(string cover, string? redirect)
    {
        var (id, mappingId) = SeedMapped("Berserk");
        var web = new Web { [cover] = redirect is null ? Ok(Png(10, 10)) : Redirect(redirect) };
        var source = Source(cover);
        await using var db = _db.NewContext();

        var result = await Controller(db).FromSource(
            id, new(mappingId), Registry(source), Availability(source), Fetcher(web), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.DoesNotContain(web.Requested, u => u.Contains("elsewhere") || u.Contains("169.254"));
        Assert.False((await Reload(id)).IsLocked(SeriesMetadataField.Cover));
    }

    [Fact]
    public async Task A_disabled_mapping_cannot_supply_the_cover()
    {
        var (id, mappingId) = SeedMapped("Berserk", enabled: false);
        var source = Source("https://cdn.fake.test/b.png");
        await using var db = _db.NewContext();

        var result = await Controller(db).FromSource(
            id, new(mappingId), Registry(source), Availability(source), Fetcher(new Web()), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task A_chapter_page_becomes_the_poster()
    {
        var (id, chapterId) = SeedChapter("Berserk", Png(60, 90), Png(120, 180));
        await using var db = _db.NewContext();
        var controller = Controller(db);

        var pages = Assert.IsType<OkObjectResult>(await controller.ChapterPages(id, chapterId, Reader(db), CancellationToken.None));
        Assert.Equal(2, (int)pages.Value!.GetType().GetProperty("pageCount")!.GetValue(pages.Value)!);

        var result = await controller.FromPage(id, new(chapterId, 1), Reader(db), Archives(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var series = await Reload(id);
        Assert.True(series.IsLocked(SeriesMetadataField.Cover));
        Assert.Equal(120, ImageCodec.IdentifyFile(series.CoverPath!).Width);
    }

    [Fact]
    public async Task A_page_out_of_range_or_of_another_series_is_not_found()
    {
        var (id, chapterId) = SeedChapter("Berserk", Png(60, 90));
        var other = _db.SeedSeries("Vagabond");
        await using var db = _db.NewContext();
        var controller = Controller(db);

        Assert.IsType<NotFoundResult>(await controller.FromPage(id, new(chapterId, 1), Reader(db), Archives(), CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.FromPage(id, new(chapterId, -1), Reader(db), Archives(), CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.FromPage(other, new(chapterId, 0), Reader(db), Archives(), CancellationToken.None));
        Assert.False((await Reload(id)).IsLocked(SeriesMetadataField.Cover));
    }

    [Theory]
    [InlineData(nameof(MediaCoverController.FromPage), Policies.EditMetadata)]
    [InlineData(nameof(MediaCoverController.FromSource), Policies.EditMetadata)]
    [InlineData(nameof(MediaCoverController.FromUrl), Policies.Admin)]
    public void Each_pick_needs_its_permission(string action, string policy)
    {
        var policies = typeof(MediaCoverController).GetMethod(action)!
            .GetCustomAttributes<AuthorizeAttribute>().Select(a => a.Policy);

        Assert.Equal([policy], policies);
    }

    private (int SeriesId, int MappingId) SeedMapped(string title, bool enabled = true)
    {
        var id = _db.SeedSeries(title, mappings: new SourceMapping
        {
            SourceName = "fake", SourceSeriesId = "b-1", Url = "https://fake.test/b-1", Enabled = enabled,
        });
        using var db = _db.NewContext();
        return (id, db.SourceMappings.Single(m => m.SeriesId == id).Id);
    }

    private (int SeriesId, int ChapterId) SeedChapter(string title, params byte[][] pages)
    {
        var id = _db.SeedSeries(title);
        var folder = Path.Combine(_configDir, "library", title);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "1.cbz");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            for (var i = 0; i < pages.Length; i++)
            {
                using var stream = archive.CreateEntry($"{i + 1:000}.png").Open();
                stream.Write(pages[i]);
            }
        }

        using var db = _db.NewContext();
        var series = db.Series.Include(s => s.RootFolder).Single(s => s.Id == id);
        series.RootFolder!.Path = folder;
        series.FolderName = "";
        var file = new ChapterFile
        {
            SeriesId = id, RelativePath = "1.cbz", Size = new FileInfo(path).Length, SourceName = "Test",
            DateAdded = DateTime.UtcNow,
        };
        db.ChapterFiles.Add(file);
        db.SaveChanges();
        var chapter = new Chapter { SeriesId = id, Number = 1, Language = "en", ChapterFileId = file.Id };
        db.Chapters.Add(chapter);
        db.SaveChanges();
        return (id, chapter.Id);
    }

    private async Task<Series> Reload(int id)
    {
        await using var db = _db.NewContext();
        return await db.Series.AsNoTracking().SingleAsync(s => s.Id == id);
    }

    private MediaCoverController Controller(MakiDbContext db) =>
        new(_paths, db, Covers(), new SeriesMetadataChangeLog(db, new RecordingInbox(), new RecordingNotifications(),
                new TestUserLocaleResolver(), new TestLocalizer(), TimeProvider.System, NullLogger<SeriesMetadataChangeLog>.Instance),
            new TestCurrentUser(1), new TestLocalizer(), Kavita(), NullLogger<MediaCoverController>.Instance);

    private ReaderService Reader(MakiDbContext db) =>
        new(db, Archives(), new ReadingProgressService(db, _gate, NullLogger<ReadingProgressService>.Instance),
            InertKavitaPusher.For(_db.ScopeFactory()), new ReadingSessionService(db), NullLogger<ReaderService>.Instance);

    private static ReaderArchiveCache Archives() => new(NullLogger<ReaderArchiveCache>.Instance);

    private static CoverFetcher Fetcher(Web web) => new(web, NullLogger<CoverFetcher>.Instance);

    private static FakeSource Source(string coverUrl) => new()
    {
        Name = "fake",
        OnGetSeries = sid => new SourceSeriesDetail(sid, "Berserk", "https://fake.test/b-1", coverUrl),
    };

    private static SourceRegistry Registry(ISource source) => new([source]);

    private static SourceAvailability Availability(ISource source) => new(new FakeAppSettings(), Registry(source));

    private KavitaScanService Kavita() =>
        new(new KavitaClient(new StubHttpClientFactory("{}")), new FakeAppSettings(), _db.ScopeFactory(),
            NullLogger<KavitaScanService>.Instance);

    private CoverService Covers() =>
        new(new StubHttpClientFactory("{}"), _paths, new FakeAppSettings(), NullLogger<CoverService>.Instance);

    private static byte[] Png(int width, int height) => new Raster(width, height).EncodePng();

    private static Func<HttpResponseMessage> Ok(byte[] body) => () => new HttpResponseMessage { Content = new ByteArrayContent(body) };

    private static Func<HttpResponseMessage> Redirect(string to) => () =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(to);
        return response;
    };

    /// <summary>Answers by exact URL, 404 otherwise, and records what was asked.</summary>
    private sealed class Web : Dictionary<string, Func<HttpResponseMessage>>, IHttpClientFactory
    {
        public List<string> Requested { get; } = [];
        public List<string> Referers { get; } = [];

        public HttpClient CreateClient(string name) => new(new Handler(this));

        private sealed class Handler(Web web) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var url = request.RequestUri!.ToString();
                web.Requested.Add(url);
                if (request.Headers.Referrer is { } referer)
                {
                    web.Referers.Add(referer.ToString());
                }

                return Task.FromResult(web.TryGetValue(url, out var answer)
                    ? answer()
                    : new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        }
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_configDir, recursive: true); } catch (IOException) { }
    }
}
