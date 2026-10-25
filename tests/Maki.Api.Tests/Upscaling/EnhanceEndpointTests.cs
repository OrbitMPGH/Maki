using System.Text.Json;
using Maki.Api.Configuration;
using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Api.Services.Upscaling;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests.Upscaling;

public sealed class EnhanceEndpointTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly TestDb _db = new();
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "maki-enhance-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly FakeAppSettings _settings = new FakeAppSettings()
        .Set(SettingKeys.UpscalingEnabled, "true")
        .Set(SettingKeys.UpscalingModels, "span-s")
        .Set(SettingKeys.UpscalingThreads, "2");
    private readonly UpscaleModelCatalog _catalog = new();
    private readonly UpscaleModelStore _store;
    private readonly UpscaleQueue _queue = new();
    private readonly UpscaleCache _cache;
    private readonly RecordingBroadcaster _broadcaster = new();
    private readonly int _chapterId;
    private readonly int _rootId;

    public EnhanceEndpointTests()
    {
        _paths = new AppPaths(_configDir);
        _store = UpscaleTestKit.StoreWithSpanS(_paths, _settings, _catalog);
        _cache = new UpscaleCache(_paths, _settings, NullLogger<UpscaleCache>.Instance);

        var root = Path.Combine(_configDir, "library");
        UpscaleTestKit.WriteCbz(Path.Combine(root, "S", "c1.cbz"), pages: 3);
        using var db = _db.NewContext();
        var folder = new RootFolder { Path = root };
        db.RootFolders.Add(folder);
        db.SaveChanges();
        _rootId = folder.Id;
        var series = new Series { Title = "S", SortTitle = "s", FolderName = "S", RootFolderId = folder.Id };
        db.Series.Add(series);
        db.SaveChanges();
        var file = new ChapterFile
        {
            SeriesId = series.Id, RelativePath = "S/c1.cbz", Size = new FileInfo(Path.Combine(root, "S", "c1.cbz")).Length,
            SourceName = "test", DateAdded = DateTime.UtcNow
        };
        db.ChapterFiles.Add(file);
        db.SaveChanges();
        var chapter = new Chapter { SeriesId = series.Id, Number = 1, ChapterFileId = file.Id };
        db.Chapters.Add(chapter);
        db.SaveChanges();
        _chapterId = chapter.Id;
    }

    public void Dispose()
    {
        _db.Dispose();
        UpscaleTestKit.TryDelete(_configDir);
    }

    private ReaderController Reader(MakiDbContext db, int userId, string? query = null)
    {
        var context = new DefaultHttpContext();
        if (query is not null)
        {
            context.Request.QueryString = new QueryString(query);
        }

        return new ReaderController(
            new TestLocalizer(), db,
            new ReaderService(db, new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance), null!, null!,
                new ReadingSessionService(db), NullLogger<ReaderService>.Instance),
            null!, null!, null!, null!, _paths, NullLogger<ReaderController>.Instance,
            new TestCurrentUser(userId), null!, _settings)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private Task<IActionResult> Post(MakiDbContext db, int userId, string model, params int[] pages) =>
        Reader(db, userId).Enhance(_chapterId, new EnhancePagesRequest(model, pages), _store, _queue, _cache, default);

    private static JsonElement Json(IActionResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value, Web);

    private static string Code(IActionResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsAssignableFrom<ObjectResult>(result).Value, Web).GetProperty("code").GetString()!;

    [Fact]
    public async Task Both_endpoints_answer_409_while_the_tier_is_off()
    {
        _settings.Set(SettingKeys.UpscalingEnabled, "false");
        using var db = _db.NewContext(1);

        var post = await Post(db, 1, "span-s", 0);
        var get = await Reader(db, 1).EnhancedPage(_chapterId, 0, "span-s", _store, _cache, default);

        Assert.Equal(409, Assert.IsType<ConflictObjectResult>(post).StatusCode);
        Assert.Equal("error.enhance.disabled", Code(post));
        Assert.Equal("error.enhance.disabled", Code(get));
    }

    [Theory]
    [InlineData("waifu2x-art")]
    [InlineData("realesrgan-av3")]
    [InlineData("nonsense")]
    public async Task A_model_not_enabled_or_not_downloaded_answers_409(string model)
    {
        _settings.Set(SettingKeys.UpscalingModels, "span-s,waifu2x-art");
        using var db = _db.NewContext(1);

        var post = await Post(db, 1, model, 0);

        Assert.IsType<ConflictObjectResult>(post);
        Assert.Equal("error.enhance.modelUnavailable", Code(post));
    }

    [Fact]
    public async Task A_page_is_404_until_the_worker_has_made_it_then_served_with_its_etag()
    {
        using var db = _db.NewContext(1);
        var before = await Reader(db, 1).EnhancedPage(_chapterId, 1, "span-s", _store, _cache, default);
        Assert.IsType<NotFoundResult>(before);
        Assert.Equal(0, _queue.Snapshot().Pending);

        var posted = Json(await Post(db, 1, "span-s", 1, 2, 99));
        Assert.Equal(
            """[{"page":1,"state":"queued"},{"page":2,"state":"queued"},{"page":99,"state":"skipped"}]""",
            posted.GetProperty("pages").GetRawText());

        using var upscaler = new OnnxPageUpscaler(_catalog, _store, _settings, NullLogger<OnnxPageUpscaler>.Instance);
        var worker = new UpscaleWorker(_queue, _cache, upscaler, new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance),
            _paths, _settings, _broadcaster, NullLogger<UpscaleWorker>.Instance);
        Assert.True(await worker.RunOnceAsync(default));
        Assert.Equal([(1, _chapterId, 1, "span-s", "ready")], _broadcaster.Sent);

        var slice = (await new ReaderService(db, new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance), null!, null!,
            new ReadingSessionService(db), NullLogger<ReaderService>.Instance).PageSliceAsync(_chapterId, default))!;
        var version = $"{slice.ChapterFileId}-{slice.ArchiveVersion}";
        var controller = Reader(db, 1, $"?model=span-s&v={version}");
        var file = Assert.IsType<PhysicalFileResult>(await controller.EnhancedPage(_chapterId, 1, "span-s", _store, _cache, default));
        Assert.Equal("image/webp", file.ContentType);
        Assert.Equal($"\"{slice.ChapterFileId}-{slice.ArchiveVersion}-1-span-s\"", file.EntityTag!.Tag.ToString());
        Assert.Equal("private, max-age=31536000, immutable", controller.Response.Headers.CacheControl.ToString());

        var stale = Reader(db, 1, "?model=span-s&v=old");
        Assert.IsType<PhysicalFileResult>(await stale.EnhancedPage(_chapterId, 1, "span-s", _store, _cache, default));
        Assert.Equal("private, no-cache", stale.Response.Headers.CacheControl.ToString());

        var again = Json(await Post(db, 1, "span-s", 1, 2));
        Assert.Equal("""[{"page":1,"state":"ready"},{"page":2,"state":"queued"}]""", again.GetProperty("pages").GetRawText());
    }

    [Fact]
    public async Task A_reader_cannot_enhance_or_fetch_pages_outside_their_root_folders()
    {
        var outsider = _db.SeedUser("outsider", MakiPermission.None, allRootFolders: false);
        using var db = _db.NewContext(outsider, allRootFolders: false);

        Assert.IsType<NotFoundResult>(await Post(db, outsider, "span-s", 0));
        Assert.IsType<NotFoundResult>(await Reader(db, outsider).EnhancedPage(_chapterId, 0, "span-s", _store, _cache, default));
        Assert.Equal(0, _queue.Snapshot().Pending);

        using (var grant = _db.NewContext())
        {
            grant.UserRootFolders.Add(new Maki.Data.Identity.UserRootFolder { UserId = outsider, RootFolderId = _rootId });
            grant.SaveChanges();
        }

        using var granted = _db.NewContext(outsider, allRootFolders: false);
        Assert.IsType<OkObjectResult>(await Post(granted, outsider, "span-s", 0));
    }

    private UpscalingController Admin() =>
        new(_settings, _catalog, _store, new OnnxPageUpscaler(_catalog, _store, _settings, NullLogger<OnnxPageUpscaler>.Instance),
            _cache, _queue, new TestLocalizer());

    [Theory]
    [InlineData(0, 1024, "error.upscaling.invalidThreads")]
    [InlineData(100000, 1024, "error.upscaling.invalidThreads")]
    [InlineData(1, 64, "error.upscaling.invalidCache")]
    [InlineData(1, 70000, "error.upscaling.invalidCache")]
    public async Task Settings_out_of_range_are_refused(int threads, int cacheMb, string key)
    {
        var result = await Admin().Save(new UpscalingController.SaveSettingsRequest(true, ["span-s"], threads, cacheMb), default);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(key, Code(result));
    }

    [Fact]
    public async Task An_unknown_model_is_refused()
    {
        var result = await Admin().Save(new UpscalingController.SaveSettingsRequest(true, ["span-s", "esrgan-9000"], 1, 1024), default);

        Assert.Equal("error.upscaling.unknownModel", Code(result));
        Assert.Equal("span-s", await _settings.GetAsync(SettingKeys.UpscalingModels));
    }

    [Fact]
    public async Task The_admin_status_has_the_planned_shape_and_save_returns_it()
    {
        await _cache.WriteAsync(new UpscaleWorkKey(1, "1-1", 0, "span-s"), new byte[10], "webp", default);

        var saved = Json(await Admin().Save(new UpscalingController.SaveSettingsRequest(true, ["span-s"], 1, 512), default));
        var status = Json(await Admin().Status(default));

        Assert.Equal(saved.GetRawText(), status.GetRawText());
        Assert.Equal(["settings", "catalog", "cache", "queue"], status.EnumerateObject().Select(p => p.Name));
        var settings = status.GetProperty("settings");
        Assert.True(settings.GetProperty("enabled").GetBoolean());
        Assert.Equal("""["span-s"]""", settings.GetProperty("models").GetRawText());
        Assert.Equal(1, settings.GetProperty("threads").GetInt32());
        Assert.Equal(512, settings.GetProperty("cacheMb").GetInt32());

        var catalog = status.GetProperty("catalog").EnumerateArray().ToList();
        Assert.Equal(["span-s", "realesrgan-av3", "waifu2x-art"], catalog.Select(c => c.GetProperty("id").GetString()));
        foreach (var name in (string[])["id", "bytes", "license", "author", "source", "status", "error"])
        {
            Assert.True(catalog[0].TryGetProperty(name, out _), name);
        }

        Assert.Equal("ready", catalog[0].GetProperty("status").GetString());
        Assert.Equal("missing", catalog[1].GetProperty("status").GetString());
        Assert.Equal(new FileInfo(UpscaleFixture.IdentityModelPath).Length, catalog[0].GetProperty("bytes").GetInt64());

        Assert.Equal(10, status.GetProperty("cache").GetProperty("bytes").GetInt64());
        Assert.Equal(1, status.GetProperty("cache").GetProperty("files").GetInt32());
        Assert.Equal(0, status.GetProperty("queue").GetProperty("pending").GetInt32());
        Assert.Equal(0, status.GetProperty("queue").GetProperty("working").GetInt32());
    }

    [Fact]
    public async Task Deleting_a_model_removes_its_file_and_its_pages()
    {
        await _cache.WriteAsync(new UpscaleWorkKey(1, "1-1", 0, "span-s"), [1], "webp", default);

        var status = Json(await Admin().Delete("span-s", default));

        Assert.False(File.Exists(_store.PathOf(UpscaleModelId.SpanS)));
        Assert.Equal("missing", status.GetProperty("catalog")[0].GetProperty("status").GetString());
        Assert.Equal(0, status.GetProperty("cache").GetProperty("files").GetInt32());
    }

    [Fact]
    public async Task The_benchmark_times_the_sample_page()
    {
        var result = Json(await Admin().Benchmark(new UpscalingController.BenchmarkRequest("span-s"), default));

        Assert.True(result.GetProperty("seconds").GetDouble() >= 0);
    }
}
