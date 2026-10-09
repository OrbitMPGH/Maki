using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Configuration;
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
            settings: new SettingsService(_db.ScopeFactory()), naming: null!, flareSolverr: null!, prowlarr: null!,
            qbittorrent: null!, kavita: null!, sourceRegistry: null!, sourceAvailability: null!,
            mangaBakaDump: null!, embeddingModel: null!, embeddingStore: null!, embeddingStatus: null!,
            embeddingIndexer: null!, prebuiltIndex: null!, recoGraph: null!,
            recoGraphCache: null!, coReadInstaller: null!, coReadCache: null!, readerCohortInstaller: null!,
            readerCohortCache: null!, tasteVectorInstaller: null!, vectorIndexCache: null!,
            modelSwitcher: null!, db: db, updateCheck: null!, currentUser: current,
            userSettings: new UserSettingsService(db, current),
            kavitaUser: null!, kavitaLive: null!, schedulerFactory: null!, scopeFactory: _db.ScopeFactory(),
            logger: NullLogger<SettingsController>.Instance);
    }

    private static SettingsController.ScrobbleSettings Body(IActionResult result) =>
        Assert.IsType<SettingsController.ScrobbleSettings>(Assert.IsType<OkObjectResult>(result).Value);

    private static SettingsController.ScrobbleSettings Request(string? password) =>
        new(null, null, null, null, null, null, null, "me@example.com", password, 30, false, null);

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
}
