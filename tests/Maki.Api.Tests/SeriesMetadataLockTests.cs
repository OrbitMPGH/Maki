using System.Reflection;
using Maki.Api.Auth;
using Maki.Api.Configuration;
using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Kavita;
using Maki.Core.Metadata;
using Maki.Core.Security;
using Maki.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Maki.Api.Tests;

/// <summary>
/// A field a user set by hand must survive every metadata writer: the refresh (daily job and the
/// on-demand button), the poster download and the image-cache rebuild. Resetting it gives it back
/// to the provider, including when the provider has nothing for it.
/// </summary>
[Collection(ConfigDirCollection.Name)]
public class SeriesMetadataLockTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _configDir;
    private readonly string? _priorEnv;
    private readonly AppPaths _paths;

    public SeriesMetadataLockTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "maki-metadata-lock-tests", Guid.NewGuid().ToString("N"));
        _priorEnv = Environment.GetEnvironmentVariable("MAKI_CONFIG_DIR");
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _configDir);
        _paths = new AppPaths();
    }

    private static SeriesMetadata ProviderValues() => new()
    {
        ProviderId = "42",
        Title = "Provider Title",
        Description = "Provider synopsis",
        Status = SeriesStatus.Completed,
        TotalChapters = 120,
        TotalVolumes = 12,
        Genres = ["Action"],
        CoverUrl = "https://example.test/cover.jpg",
    };

    private static Series UserSeries(SeriesMetadataField locked) => new()
    {
        Id = 7,
        MangaBakaId = 42,
        Title = "My Title",
        SortTitle = "my title",
        Overview = "My synopsis",
        Status = SeriesStatus.Ongoing,
        TotalChapters = 5,
        TotalVolumes = 1,
        Genres = ["Drama"],
        LockedFields = locked,
    };

    private static readonly SeriesMetadataField[] Lockable =
    [
        SeriesMetadataField.Title, SeriesMetadataField.Overview, SeriesMetadataField.Status,
        SeriesMetadataField.TotalChapters, SeriesMetadataField.TotalVolumes, SeriesMetadataField.Genres,
    ];

    public static TheoryData<SeriesMetadataField> LockableFields => new(Lockable);

    private static object? ValueOf(Series s, SeriesMetadataField field) => field switch
    {
        SeriesMetadataField.Title => s.Title,
        SeriesMetadataField.Overview => s.Overview,
        SeriesMetadataField.Status => s.Status,
        SeriesMetadataField.TotalChapters => s.TotalChapters,
        SeriesMetadataField.TotalVolumes => s.TotalVolumes,
        SeriesMetadataField.Genres => string.Join(",", s.Genres),
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };

    [Theory]
    [MemberData(nameof(LockableFields))]
    public async Task A_refresh_keeps_a_locked_field_and_updates_the_rest(SeriesMetadataField field)
    {
        var series = UserSeries(field);
        var before = ValueOf(series, field);

        Assert.True(await Refresh(ProviderValues()).RefreshAsync(series, includeCover: false));

        Assert.Equal(before, ValueOf(series, field));
        foreach (var other in Lockable.Where(f => f != field))
        {
            Assert.NotEqual(ValueOf(UserSeries(SeriesMetadataField.None), other), ValueOf(series, other));
        }
    }

    [Fact]
    public async Task A_locked_title_keeps_its_sort_title()
    {
        var series = UserSeries(SeriesMetadataField.Title);

        await Refresh(ProviderValues()).RefreshAsync(series, includeCover: false);

        Assert.Equal("my title", series.SortTitle);
    }

    [Fact]
    public async Task A_restored_field_takes_an_empty_provider_value()
    {
        var series = UserSeries(SeriesMetadataField.None);
        var empty = ProviderValues() with { Description = null, TotalChapters = null };

        await Refresh(empty).RefreshAsync(
            series, includeCover: false, SeriesMetadataField.Overview | SeriesMetadataField.TotalChapters);

        Assert.Null(series.Overview);
        Assert.Null(series.TotalChapters);
        // Not restored, so the ordinary keep-what-we-have rule still applies.
        Assert.Equal(12, series.TotalVolumes);
    }

    [Fact]
    public async Task Editing_sets_the_value_and_locks_it()
    {
        var id = _db.SeedSeries("Berserk", configure: s => s.MangaBakaId = 42);
        await using var db = _db.NewContext();

        var result = await Controller(db, ProviderValues()).Edit(id, new SeriesMetadataController.EditMetadataRequest(
            ["title", "status", "totalChapters", "genres", "overview"],
            Title: "  Berserk Deluxe ", Status: "hiatus", TotalChapters: 380, Genres: ["Dark Fantasy", " ", "dark fantasy"],
            Overview: "  "), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var series = await Reload(id);
        Assert.Equal("Berserk Deluxe", series.Title);
        Assert.Equal("berserk deluxe", series.SortTitle);
        Assert.Equal(SeriesStatus.Hiatus, series.Status);
        Assert.Equal(380, series.TotalChapters);
        Assert.Equal(["Dark Fantasy"], series.Genres);
        Assert.Null(series.Overview);
        Assert.Equal(
            SeriesMetadataField.Title | SeriesMetadataField.Status | SeriesMetadataField.TotalChapters |
            SeriesMetadataField.Genres | SeriesMetadataField.Overview,
            series.LockedFields);
    }

    [Theory]
    [InlineData("cover")]
    [InlineData("year")]
    [InlineData("4")]
    public async Task Editing_refuses_a_field_that_cannot_be_set_by_hand(string field)
    {
        var id = _db.SeedSeries("Berserk");
        await using var db = _db.NewContext();

        var result = await Controller(db, ProviderValues()).Edit(
            id, new SeriesMetadataController.EditMetadataRequest([field], Title: "x"), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(SeriesMetadataField.None, (await Reload(id)).LockedFields);
    }

    [Theory]
    [InlineData("title", null, null, null)]
    [InlineData("status", null, "7", null)]
    [InlineData("status", null, "Finished", null)]
    [InlineData("totalChapters", null, null, -1)]
    public async Task Editing_validates_values(string field, string? title, string? status, int? chapters)
    {
        var id = _db.SeedSeries("Berserk");
        await using var db = _db.NewContext();

        var result = await Controller(db, ProviderValues()).Edit(id, new SeriesMetadataController.EditMetadataRequest(
            [field], Title: title, Status: status, TotalChapters: chapters), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Berserk", (await Reload(id)).Title);
    }

    [Fact]
    public async Task Resetting_unlocks_and_restores_the_provider_value()
    {
        var id = _db.SeedSeries("My Title", configure: s =>
        {
            s.MangaBakaId = 42;
            s.Status = SeriesStatus.Hiatus;
            s.LockedFields = SeriesMetadataField.Title | SeriesMetadataField.Status;
        });
        await using var db = _db.NewContext();

        var result = await Controller(db, ProviderValues()).Reset(
            id, new SeriesMetadataController.ResetMetadataRequest(["title"]), CancellationToken.None);

        var state = Assert.IsType<SeriesMetadataController.MetadataStateDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.True(state.Refreshed);
        Assert.Equal(["status"], state.LockedFields);
        var series = await Reload(id);
        Assert.Equal("Provider Title", series.Title);
        Assert.Equal(SeriesStatus.Hiatus, series.Status);
        Assert.Equal(SeriesMetadataField.Status, series.LockedFields);
    }

    [Fact]
    public async Task A_refresh_never_replaces_a_user_cover()
    {
        var series = UserSeries(SeriesMetadataField.Cover);
        WriteCover(series.Id, Jpeg(8));
        var covers = Covers();

        await new SeriesMetadataRefreshService([new Provider(ProviderValues())], covers)
            .RefreshAsync(series, includeCover: true);
        Assert.False(await new SeriesMetadataRefreshService([new Provider(ProviderValues())], covers)
            .RefreshCoverAsync(series));

        Assert.Equal(8, Image.Identify(covers.CoverPathFor(series.Id)).Width);
    }

    [Fact]
    public async Task Resetting_the_cover_downloads_the_provider_poster()
    {
        var id = _db.SeedSeries("Berserk", configure: s =>
        {
            s.MangaBakaId = 42;
            s.LockedFields = SeriesMetadataField.Cover;
        });
        WriteCover(id, Jpeg(8));
        await using var db = _db.NewContext();

        await Controller(db, ProviderValues()).Reset(
            id, new SeriesMetadataController.ResetMetadataRequest(["cover"]), CancellationToken.None);

        Assert.Equal(4, Image.Identify(Covers().CoverPathFor(id)).Width);
        Assert.Equal(SeriesMetadataField.None, (await Reload(id)).LockedFields);
    }

    [Fact]
    public async Task The_image_cache_rebuild_skips_a_user_cover_even_when_forced()
    {
        var locked = _db.SeedSeries("Locked", configure: s =>
        {
            s.MangaBakaId = 42;
            s.LockedFields = SeriesMetadataField.Cover;
        });
        var missing = _db.SeedSeries("LockedMissing", configure: s =>
        {
            s.MangaBakaId = 42;
            s.LockedFields = SeriesMetadataField.Cover;
        });
        WriteCover(locked, Jpeg(8));
        var covers = Covers();
        var status = new ImageCacheRebuildStatus();
        await using var db = _db.NewContext();
        var rebuild = new ImageCacheRebuildService(
            db, _paths, covers, new SeriesMetadataRefreshService([new Provider(ProviderValues())], covers), status,
            NullLogger<ImageCacheRebuildService>.Instance);

        Assert.True(await rebuild.RunAsync(force: true, CancellationToken.None));

        Assert.Equal(8, Image.Identify(covers.CoverPathFor(locked)).Width);
        Assert.False(File.Exists(covers.CoverPathFor(missing)));
        Assert.Equal(2, status.Snapshot().Skipped);
    }

    [Fact]
    public async Task An_uploaded_cover_is_stored_resized_and_locked()
    {
        var id = _db.SeedSeries("Berserk");
        await using var db = _db.NewContext();

        var result = await CoverController(db).Upload(id, FormFile(Png(900, 1200)), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var series = await Reload(id);
        Assert.True(series.IsLocked(SeriesMetadataField.Cover));
        Assert.Equal(Covers().CoverPathFor(id), series.CoverPath);
        var stored = Image.Identify(series.CoverPath!);
        Assert.Equal(400, stored.Width);
        Assert.Equal("JPEG", stored.Metadata.DecodedImageFormat?.Name);

        await using var read = _db.NewContext();
        var change = Assert.Single(read.SeriesMetadataChanges.Where(c => c.SeriesId == id));
        Assert.Equal((SeriesMetadataField.Cover, MetadataChangeSource.User, 1), (change.Field, change.Source, change.UserId));
    }

    public static TheoryData<string, byte[]> NotImages => new()
    {
        { "html", "<html><script>alert(1)</script></html>"u8.ToArray() },
        { "svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8.ToArray() },
        { "truncated png", Png(10, 10)[..24] },
        { "too many pixels", Png(MediaCoverController.MaxCoverDimension + 1, 1) },
    };

    [Theory]
    [MemberData(nameof(NotImages))]
    public async Task An_upload_that_is_not_a_usable_image_is_refused(string what, byte[] bytes)
    {
        var id = _db.SeedSeries("Berserk");
        await using var db = _db.NewContext();

        var result = await CoverController(db).Upload(id, FormFile(bytes), CancellationToken.None);

        Assert.True(result is BadRequestObjectResult, what);
        var series = await Reload(id);
        Assert.False(series.IsLocked(SeriesMetadataField.Cover));
        Assert.False(File.Exists(Covers().CoverPathFor(id)));
    }

    [Fact]
    public async Task An_empty_upload_is_refused()
    {
        var id = _db.SeedSeries("Berserk");
        await using var db = _db.NewContext();

        Assert.IsType<BadRequestObjectResult>(await CoverController(db).Upload(id, null, CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await CoverController(db).Upload(id, FormFile([]), CancellationToken.None));
    }

    [Fact]
    public async Task An_upload_for_a_series_out_of_reach_is_not_found()
    {
        var id = _db.SeedSeries("Berserk");
        var reader = _db.SeedUser("restricted", MakiPermission.EditMetadata, allRootFolders: false);
        await using var db = _db.NewContext(reader, allRootFolders: false);

        Assert.IsType<NotFoundResult>(await CoverController(db).Upload(id, FormFile(Png(10, 10)), CancellationToken.None));
        Assert.False(File.Exists(Covers().CoverPathFor(id)));
    }

    [Theory]
    [InlineData(typeof(SeriesMetadataController), nameof(SeriesMetadataController.Edit))]
    [InlineData(typeof(SeriesMetadataController), nameof(SeriesMetadataController.Reset))]
    [InlineData(typeof(MediaCoverController), nameof(MediaCoverController.Upload))]
    public void Every_metadata_write_needs_EditMetadata(Type controller, string action)
    {
        var policies = controller.GetMethod(action)!.GetCustomAttributes<AuthorizeAttribute>().Select(a => a.Policy);

        Assert.Equal([Policies.EditMetadata], policies);
    }

    private async Task<Series> Reload(int id)
    {
        await using var db = _db.NewContext();
        return await db.Series.AsNoTracking().SingleAsync(s => s.Id == id);
    }

    private SeriesMetadataController Controller(MakiDbContext db, SeriesMetadata metadata)
    {
        var covers = Covers();
        return new SeriesMetadataController(
            new TestLocalizer(), db, new SeriesMetadataRefreshService([new Provider(metadata)], covers),
            ChangeLog(db), new SeriesIdentityService(db, NullLogger<SeriesIdentityService>.Instance),
            new TestCurrentUser(1), Kavita());
    }

    private MediaCoverController CoverController(MakiDbContext db) =>
        new(_paths, db, Covers(), ChangeLog(db), new TestCurrentUser(1), new TestLocalizer(), Kavita(),
            NullLogger<MediaCoverController>.Instance);

    private static SeriesMetadataChangeLog ChangeLog(MakiDbContext db) =>
        new(db, new RecordingInbox(), new RecordingNotifications(), new TestUserLocaleResolver(), new TestLocalizer(),
            TimeProvider.System, NullLogger<SeriesMetadataChangeLog>.Instance);

    private KavitaScanService Kavita() =>
        new(new KavitaClient(new StubHttpClientFactory("{}")), new FakeAppSettings(), _db.ScopeFactory(),
            NullLogger<KavitaScanService>.Instance);

    private CoverService Covers() =>
        new(new JpegClientFactory(Jpeg(4)), _paths, new FakeAppSettings(), NullLogger<CoverService>.Instance);

    private static SeriesMetadataRefreshService Refresh(SeriesMetadata metadata) =>
        new([new Provider(metadata)], null!);

    private void WriteCover(int seriesId, byte[] bytes)
    {
        var path = Covers().CoverPathFor(seriesId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static IFormFile FormFile(byte[] bytes) =>
        new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "cover.png");

    private static byte[] Jpeg(int size)
    {
        using var image = new Image<Rgba32>(size, size);
        using var buffer = new MemoryStream();
        image.SaveAsJpeg(buffer);
        return buffer.ToArray();
    }

    private static byte[] Png(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height);
        using var buffer = new MemoryStream();
        image.SaveAsPng(buffer);
        return buffer.ToArray();
    }

    private sealed class Provider(SeriesMetadata metadata) : IMetadataProvider
    {
        public string Name => "stub";

        public Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(
            string query, string maxContentRating, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MetadataSearchResult>>([]);

        public Task<SeriesMetadata?> GetAsync(string providerId, CancellationToken ct = default) =>
            Task.FromResult<SeriesMetadata?>(metadata);
    }

    private sealed class JpegClientFactory(byte[] bytes) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(bytes));

        private sealed class Handler(byte[] bytes) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                Task.FromResult(new HttpResponseMessage { Content = new ByteArrayContent(bytes) });
        }
    }

    public void Dispose()
    {
        _db.Dispose();
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _priorEnv);
        try { Directory.Delete(_configDir, recursive: true); } catch (IOException) { }
    }
}
