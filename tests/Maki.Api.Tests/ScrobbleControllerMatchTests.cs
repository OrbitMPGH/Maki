using Maki.Api.Controllers;
using Maki.Api.Services;
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

        return new ScrobbleController(
            new TestLocalizer(), service, animeSources: null!, tokens, settings: null!, userSettings: null!,
            _db.NewContext(userId), new TestCurrentUser(userId, permissions: MakiPermission.UseTrackers),
            schedulerFactory: null!);
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
        Assert.Contains("error.scrobble.invalidRemoteId", bad.Value!.ToString());
        Assert.Null(MappedRemoteId(userId, "mangaupdates"));
    }

    [Fact]
    public async Task Other_services_keep_the_digits_rule()
    {
        var userId = _db.SeedUser("reader", MakiPermission.UseTrackers);

        Assert.IsType<OkObjectResult>(await Controller(userId).Match(new(7, "anilist", "30013"), default));
        Assert.Equal("30013", MappedRemoteId(userId, "anilist"));

        Assert.IsType<BadRequestObjectResult>(await Controller(userId).Match(new(8, "anilist", "pb8uwds"), default));
    }
}
