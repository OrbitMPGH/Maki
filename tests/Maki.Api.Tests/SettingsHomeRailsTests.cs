using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// <c>SettingsController</c>'s UI endpoint merges the stored Home layout against the caller's own
/// custom rails, so a <c>rail:{id}</c> key means something. <see cref="TitleLanguageTests"/> is the
/// sibling file for the rest of this controller's UI surface.
/// </summary>
public sealed class SettingsHomeRailsTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private SettingsController Controller(int userId)
    {
        var db = _db.NewContext(userId);
        return new SettingsController(
            localizer: new TestLocalizer(), userLocales: new TestUserLocaleResolver(),
            settings: null!, naming: null!, flareSolverr: null!, prowlarr: null!, qbittorrent: null!,
            kavita: null!, configFile: null!, sourceRegistry: null!, sourceAvailability: null!,
            mangaBakaDump: null!, embeddingModel: null!, embeddingStore: null!, embeddingStatus: null!,
            embeddingIndexer: null!, embeddingOptions: null!, prebuiltIndex: null!, recoGraph: null!,
            recoGraphCache: null!, coReadInstaller: null!, coReadCache: null!, readerCohortInstaller: null!,
            readerCohortCache: null!, tasteVectorInstaller: null!, vectorIndexCache: null!,
            modelSwitcher: null!, db: db, updateCheck: null!, currentUser: new TestCurrentUser(userId),
            userSettings: new UserSettingsService(db, new TestCurrentUser(userId)),
            kavitaUser: null!, schedulerFactory: null!, scopeFactory: _db.ScopeFactory(),
            logger: NullLogger<SettingsController>.Instance);
    }

    private static SettingsController.UiSettings Body(IActionResult result) =>
        Assert.IsType<SettingsController.UiSettings>(Assert.IsType<OkObjectResult>(result).Value);

    /// <summary>Seeds a Home-placed custom rail directly, without going through the rails controller.</summary>
    private int SeedHomeRail(int userId, string name = "My rail", int sortOrder = 0)
    {
        using var db = _db.NewContext(userId);
        var rail = new SavedFilter
        {
            Name = name,
            Scope = SavedFilter.HomeRailScope,
            Spec = CustomRailSpec.Serialize(new CustomRailSpec(Source: CustomRailSources.Catalogue)),
            SortOrder = sortOrder,
            Created = DateTime.UtcNow,
        };
        db.SavedFilters.Add(rail);
        db.SaveChanges();
        return rail.Id;
    }

    [Fact]
    public async Task GetUi_includes_a_rail_key_for_each_of_the_callers_home_rails()
    {
        var alice = _db.SeedUser("alice");
        var railId = SeedHomeRail(alice);

        var ui = Body(await Controller(alice).GetUi(CancellationToken.None));

        Assert.Contains(ui.HomeLayout.Sections!, s => s.Key == HomeSections.RailKey(railId));
    }

    [Fact]
    public async Task GetUi_does_not_leak_another_users_home_rail()
    {
        var alice = _db.SeedUser("alice");
        var bob = _db.SeedUser("bob");
        var railId = SeedHomeRail(bob);

        var ui = Body(await Controller(alice).GetUi(CancellationToken.None));

        Assert.DoesNotContain(ui.HomeLayout.Sections!, s => s.Key == HomeSections.RailKey(railId));
    }

    [Fact]
    public async Task SetUi_keeps_the_callers_chosen_rail_order()
    {
        var alice = _db.SeedUser("alice");
        var first = SeedHomeRail(alice, "First", sortOrder: 0);
        var second = SeedHomeRail(alice, "Second", sortOrder: 1);

        // Ask for the layout the way a reorder in the client would: second rail ahead of first.
        var requested = new SettingsController.UiSettings("library", new HomeLayoutSpec(true,
        [
            new HomeSection(HomeSections.RailKey(second)),
            new HomeSection(HomeSections.RailKey(first)),
        ]));

        var written = Body(await Controller(alice).SetUi(requested, CancellationToken.None));
        var reread = Body(await Controller(alice).GetUi(CancellationToken.None));

        Assert.Equal(
            [HomeSections.RailKey(second), HomeSections.RailKey(first)],
            written.HomeLayout.Sections!.Take(2).Select(s => s.Key));
        Assert.Equal(
            [HomeSections.RailKey(second), HomeSections.RailKey(first)],
            reread.HomeLayout.Sections!.Take(2).Select(s => s.Key));
    }
}
