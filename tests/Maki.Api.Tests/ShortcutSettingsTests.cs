using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>The per-user keyboard shortcut overrides: stored per user, bounded, and empty by default.</summary>
public sealed class ShortcutSettingsTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private SettingsController Settings(int userId)
    {
        var db = _db.NewContext(userId);
        return new SettingsController(
            localizer: new TestLocalizer(), userLocales: new TestUserLocaleResolver(),
            settings: null!, naming: null!, flareSolverr: null!, indexers: null!, torrentClients: null!,
            kavita: null!, sourceRegistry: null!, sourceAvailability: null!,
            mangaBakaDump: null!, embeddingModel: null!, embeddingStore: null!, embeddingStatus: null!,
            embeddingIndexer: null!, prebuiltIndex: null!, recoGraph: null!,
            recoGraphCache: null!, coReadInstaller: null!, coReadCache: null!, easeInstaller: null!, easeCache: null!,
            readerCohortInstaller: null!,
            readerCohortCache: null!, tasteVectorInstaller: null!, vectorIndexCache: null!,
            modelSwitcher: null!, db: db, updateCheck: null!, currentUser: new TestCurrentUser(userId),
            userSettings: new UserSettingsService(db, new TestCurrentUser(userId)),
            kavitaUser: null!, kavitaLive: null!, schedulerFactory: null!, scopeFactory: _db.ScopeFactory(),
            logger: NullLogger<SettingsController>.Instance);
    }

    private async Task<SettingsController.ShortcutSettings> Get(int userId) =>
        Assert.IsType<SettingsController.ShortcutSettings>(
            Assert.IsType<OkObjectResult>(await Settings(userId).GetShortcuts(default)).Value);

    [Fact]
    public async Task Shortcuts_default_to_no_overrides()
    {
        var alice = _db.SeedUser("alice");

        Assert.Empty((await Get(alice)).Bindings);
    }

    [Fact]
    public async Task Shortcuts_round_trip_trimmed_and_stay_per_user()
    {
        var alice = _db.SeedUser("alice");
        var bob = _db.SeedUser("bob");
        var request = new SettingsController.ShortcutSettingsRequest(new()
        {
            ["reader.next"] = [" j ", "j", "Shift+ArrowDown"],
            ["reader.bookmark"] = [],
        });

        Assert.IsType<OkObjectResult>(await Settings(alice).SetShortcuts(request, default));

        var saved = (await Get(alice)).Bindings;
        Assert.Equal(["j", "Shift+ArrowDown"], saved["reader.next"]);
        Assert.Empty(saved["reader.bookmark"]);
        Assert.Empty((await Get(bob)).Bindings);

        Assert.IsType<OkObjectResult>(await Settings(alice).SetShortcuts(new(new()), default));
        Assert.Empty((await Get(alice)).Bindings);
        Assert.Null(await new UserSettingsService(_db.NewContext(alice), new TestCurrentUser(alice))
            .GetAsync(SettingKeys.UiShortcuts));
    }

    [Theory]
    [InlineData("bad id", "k")]
    [InlineData("reader.next", "")]
    [InlineData("reader.next", "this key name is far too long to be any real key")]
    public async Task Shortcuts_refuse_a_bad_action_or_key(string action, string key)
    {
        var alice = _db.SeedUser("alice");
        var request = new SettingsController.ShortcutSettingsRequest(new() { [action] = [key] });

        Assert.IsType<BadRequestObjectResult>(await Settings(alice).SetShortcuts(request, default));
    }

    [Fact]
    public async Task Shortcuts_refuse_too_many_keys_for_one_action()
    {
        var alice = _db.SeedUser("alice");
        var request = new SettingsController.ShortcutSettingsRequest(new() { ["reader.next"] = ["a", "b", "c", "d", "e"] });

        Assert.IsType<BadRequestObjectResult>(await Settings(alice).SetShortcuts(request, default));
    }
}
