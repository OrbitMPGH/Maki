using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Scrobbling;
using Maki.Core.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public sealed class ScrobbleSettingsSecretTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private SettingsController Controller(int userId)
    {
        var db = _db.NewContext(userId);
        var current = new TestCurrentUser(userId, permissions: MakiPermission.UseTrackers);
        return new SettingsController(
            localizer: new TestLocalizer(), userLocales: new TestUserLocaleResolver(),
            settings: new SettingsService(_db.ScopeFactory()), naming: null!, flareSolverr: null!, indexers: null!,
            torrentClients: null!, kavita: null!, sourceRegistry: null!, sourceAvailability: null!,
            mangaBakaDump: null!, embeddingModel: null!, embeddingStore: null!, embeddingStatus: null!,
            embeddingIndexer: null!, prebuiltIndex: null!, recoGraph: null!,
            recoGraphCache: null!, coReadInstaller: null!, coReadCache: null!, readerCohortInstaller: null!,
            readerCohortCache: null!, tasteVectorInstaller: null!, vectorIndexCache: null!,
            modelSwitcher: null!, db: db, updateCheck: null!, currentUser: current,
            userSettings: new UserSettingsService(db, current),
            kavitaUser: null!, kavitaLive: null!, schedulerFactory: null!, scopeFactory: _db.ScopeFactory(),
            logger: NullLogger<SettingsController>.Instance,
            scrobbleTokens: new ScrobbleTokenStore(_db.ScopeFactory()),
            mangaUpdates: new MangaUpdatesTracker(
                new NoHttpClientFactory(), new UserSettingsStoreService(_db.ScopeFactory()),
                new ScrobbleTokenStore(_db.ScopeFactory()), new ScrobbleTrackerOptions(),
                NullLogger<MangaUpdatesTracker>.Instance));
    }

    private sealed class NoHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new NotSupportedException();
    }

    private static SettingsController.ScrobbleSettings Body(IActionResult result) =>
        Assert.IsType<SettingsController.ScrobbleSettings>(Assert.IsType<OkObjectResult>(result).Value);

    private static SettingsController.ScrobbleSettings Request(string? password, string? token = null) =>
        new(null, null, null, null, token, null, null, "me@example.com", password, 30, false, null);

    private static SettingsController.ScrobbleSettings MuRequest(string? username, string? password) =>
        new(null, null, null, null, null, null, null, null, null, 30, false, null,
            MangaUpdatesUsername: username, MangaUpdatesPassword: password);

    private async Task SeedMuTokenAsync(int userId, string service = "mangaupdates")
    {
        await new ScrobbleTokenStore(_db.ScopeFactory()).SaveAsync(new ScrobbleToken
        {
            UserId = userId, Service = service, AccessToken = "session", Username = "someone",
            ExpiresAt = DateTime.UtcNow.AddDays(30),
        });
    }

    private async Task<bool> HasTokenAsync(int userId, string service = "mangaupdates") =>
        await new ScrobbleTokenStore(_db.ScopeFactory()).GetAsync(userId, service) is not null;

    [Fact]
    public async Task The_kitsu_password_is_never_returned_only_whether_one_is_set()
    {
        var userId = _db.SeedUser("reader", MakiPermission.UseTrackers);

        var saved = Body(await Controller(userId).SetScrobble(Request("hunter2"), default));
        var read = Body(await Controller(userId).GetScrobble(default));

        Assert.Null(saved.KitsuPassword);
        Assert.Null(read.KitsuPassword);
        Assert.True(read.KitsuPasswordSet);
    }

    [Fact]
    public async Task A_save_without_a_password_keeps_the_stored_one_and_an_empty_one_clears_it()
    {
        var userId = _db.SeedUser("reader", MakiPermission.UseTrackers);
        await Controller(userId).SetScrobble(Request("hunter2"), default);

        await Controller(userId).SetScrobble(Request(null), default);
        Assert.True(Body(await Controller(userId).GetScrobble(default)).KitsuPasswordSet);

        await Controller(userId).SetScrobble(Request(""), default);
        Assert.False(Body(await Controller(userId).GetScrobble(default)).KitsuPasswordSet);
    }

    [Fact]
    public async Task The_mangaupdates_password_is_never_returned_and_follows_the_null_and_empty_rules()
    {
        var userId = _db.SeedUser("reader", MakiPermission.UseTrackers);

        var saved = Body(await Controller(userId).SetScrobble(MuRequest("muuser", "hunter2"), default));
        Assert.Equal("muuser", saved.MangaUpdatesUsername);
        Assert.Null(saved.MangaUpdatesPassword);
        Assert.True(saved.MangaUpdatesPasswordSet);

        var untouched = Body(await Controller(userId).SetScrobble(MuRequest(null, null), default));
        Assert.Equal("muuser", untouched.MangaUpdatesUsername);
        Assert.True(untouched.MangaUpdatesPasswordSet);

        var cleared = Body(await Controller(userId).SetScrobble(MuRequest("", ""), default));
        Assert.Null(cleared.MangaUpdatesUsername);
        Assert.Null(cleared.MangaUpdatesPassword);
        Assert.False(cleared.MangaUpdatesPasswordSet);
    }

    [Fact]
    public async Task Changing_the_mangaupdates_credentials_drops_the_stored_session_token()
    {
        var userId = _db.SeedUser("reader", MakiPermission.UseTrackers);
        var other = _db.SeedUser("other", MakiPermission.UseTrackers);
        await Controller(userId).SetScrobble(MuRequest("muuser", "hunter2"), default);

        await SeedMuTokenAsync(userId);
        await SeedMuTokenAsync(other);
        await SeedMuTokenAsync(userId, "kitsu");

        // Same values again and a save that sends nothing: the session is still good.
        await Controller(userId).SetScrobble(MuRequest("muuser", "hunter2"), default);
        await Controller(userId).SetScrobble(MuRequest(null, null), default);
        Assert.True(await HasTokenAsync(userId));

        await Controller(userId).SetScrobble(MuRequest(null, "new-password"), default);
        Assert.False(await HasTokenAsync(userId));
        Assert.True(await HasTokenAsync(other));
        Assert.True(await HasTokenAsync(userId, "kitsu"));

        await SeedMuTokenAsync(userId);
        await Controller(userId).SetScrobble(MuRequest("renamed", null), default);
        Assert.False(await HasTokenAsync(userId));

        await SeedMuTokenAsync(userId);
        await Controller(userId).SetScrobble(MuRequest("", null), default);
        Assert.False(await HasTokenAsync(userId));
    }

    [Fact]
    public async Task The_mangabaka_token_is_never_returned_and_follows_the_same_null_and_empty_rules()
    {
        var userId = _db.SeedUser("reader", MakiPermission.UseTrackers);

        var saved = Body(await Controller(userId).SetScrobble(Request(null, "mb-secret"), default));
        Assert.Null(saved.MangaBakaToken);
        Assert.True(saved.MangaBakaTokenSet);

        await Controller(userId).SetScrobble(Request(null, null), default);
        Assert.True(Body(await Controller(userId).GetScrobble(default)).MangaBakaTokenSet);

        await Controller(userId).SetScrobble(Request(null, ""), default);
        var read = Body(await Controller(userId).GetScrobble(default));
        Assert.False(read.MangaBakaTokenSet);
        Assert.Null(read.MangaBakaToken);
    }
}
