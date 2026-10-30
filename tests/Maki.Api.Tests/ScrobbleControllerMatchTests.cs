using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Scrobbling;
using Maki.Core.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public sealed class ScrobbleControllerMatchTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private ScrobbleController Controller(int userId)
    {
        var http = new NoHttpClientFactory();
        var appSettings = new FakeAppSettings();
        var tokens = new ScrobbleTokenStore(_db.ScopeFactory());
        var userSettings = new UserSettingsStoreService(_db.ScopeFactory());
        var options = new ScrobbleTrackerOptions();
        var service = new ScrobbleService(
            _db.ScopeFactory(), settings: null!, userSettings, kavitaUser: null!, kavita: null!,
            volumeBoundaries: null!,
            anilist: new AniListTracker(http, appSettings, tokens, options, NullLogger<AniListTracker>.Instance),
            mal: new MalTracker(http, appSettings, tokens, options, NullLogger<MalTracker>.Instance),
            mangaBaka: new MangaBakaTracker(
                http, userSettings, tokens, options, NullLogger<MangaBakaTracker>.Instance),
            kitsu: new KitsuTracker(
                http, appSettings, userSettings, tokens, options, NullLogger<KitsuTracker>.Instance),
            mangaUpdates: new MangaUpdatesTracker(
                http, userSettings, tokens, options, NullLogger<MangaUpdatesTracker>.Instance),
            logger: NullLogger<ScrobbleService>.Instance);

        var db = _db.NewContext(userId);
        var currentUser = new TestCurrentUser(userId, permissions: MakiPermission.UseTrackers);
        return new ScrobbleController(
            new TestLocalizer(), service, animeSources: null!, tokens, settings: null!,
            new UserSettingsService(db, currentUser), db, currentUser, schedulerFactory: null!);
    }

    private sealed class NoHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new NotSupportedException();
    }

    private string? MappedRemoteId(int userId, string service)
    {
        using var db = _db.NewContext(userId);
        return db.ScrobbleMappings.AsNoTracking()
            .Where(m => m.UserId == userId && m.Service == service)
            .Select(m => m.RemoteId)
            .SingleOrDefault();
    }

    private string? UserSetting(int userId, string key)
    {
        using var db = _db.NewContext(userId);
        return db.UserSettings.AsNoTracking().Where(s => s.UserId == userId && s.Key == key)
            .Select(s => s.Value).SingleOrDefault();
    }

    [Fact]
    public async Task Turning_so_far_sync_on_skips_a_tracker_with_rating_sync_off()
    {
        var userId = _db.SeedUser("reader", MakiPermission.UseTrackers);
        await new UserSettingsStoreService(_db.ScopeFactory())
            .SetAsync(userId, SettingKeys.ScrobbleRatingsKey("anilist"), "false");

        await Controller(userId).SetSoFarSync(new(["anilist", "mangabaka"], Enabled: true), default);

        // Left unset, so AniList still gets the question once its rating sync is back on.
        Assert.Null(UserSetting(userId, SettingKeys.ScrobbleSoFarKey("anilist")));
        Assert.Equal("true", UserSetting(userId, SettingKeys.ScrobbleSoFarKey("mangabaka")));
    }

    [Fact]
    public async Task Turning_so_far_sync_off_is_stored_whatever_rating_sync_says()
    {
        var userId = _db.SeedUser("reader", MakiPermission.UseTrackers);
        await new UserSettingsStoreService(_db.ScopeFactory())
            .SetAsync(userId, SettingKeys.ScrobbleRatingsKey("anilist"), "false");

        await Controller(userId).SetSoFarSync(new(["anilist"], Enabled: false), default);

        Assert.Equal("false", UserSetting(userId, SettingKeys.ScrobbleSoFarKey("anilist")));
    }

    [Theory]
    [InlineData("pb8uwds", "pb8uwds")]
    [InlineData("PB8UWDS", "pb8uwds")]
    [InlineData("https://www.mangaupdates.com/series/pb8uwds/one-piece", "pb8uwds")]
    [InlineData("6028577", "6028577")]
    public async Task A_mangaupdates_slug_or_url_is_saved_lowercase(string input, string expected)
    {
        var userId = _db.SeedUser("reader", MakiPermission.UseTrackers);

        var result = await Controller(userId).Match(new(7, "mangaupdates", input), default);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(expected, MappedRemoteId(userId, "mangaupdates"));
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("602857")]
    [InlineData("https://www.mangaupdates.com/series.html?id=12345")]
    [InlineData("not a slug!")]
    [InlineData("")]
    public async Task A_legacy_numeric_id_or_junk_is_refused_for_mangaupdates(string input)
    {
        var userId = _db.SeedUser("reader", MakiPermission.UseTrackers);

        var result = await Controller(userId).Match(new(7, "mangaupdates", input), default);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("error.scrobble.invalidMangaUpdatesId", bad.Value!.ToString());
        Assert.Null(MappedRemoteId(userId, "mangaupdates"));
    }

    [Fact]
    public async Task Other_services_keep_the_digits_rule()
    {
        var userId = _db.SeedUser("reader", MakiPermission.UseTrackers);

        Assert.IsType<OkObjectResult>(await Controller(userId).Match(new(7, "anilist", "30013"), default));
        Assert.Equal("30013", MappedRemoteId(userId, "anilist"));

        var bad = Assert.IsType<BadRequestObjectResult>(
            await Controller(userId).Match(new(8, "anilist", "pb8uwds"), default));
        Assert.Contains("error.scrobble.invalidRemoteId", bad.Value!.ToString());
    }
}
