using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// <c>SettingsController</c>'s UI endpoint merges the stored Home and Discover layouts against the
/// caller's own custom rails, so a <c>rail:{id}</c> key means something. <see cref="TitleLanguageTests"/>
/// is the sibling file for the rest of this controller's UI surface.
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
            kavita: null!, sourceRegistry: null!, sourceAvailability: null!,
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
    private int SeedHomeRail(int userId, string name = "My rail", int sortOrder = 0) =>
        SeedRail(userId, SavedFilter.HomeRailScope, name, sortOrder);

    /// <summary>Seeds a Discover-placed custom rail directly, without going through the rails controller.</summary>
    private int SeedDiscoverRail(int userId, string name = "My discover rail", int sortOrder = 0) =>
        SeedRail(userId, SavedFilter.DiscoverRailScope, name, sortOrder);

    private int SeedRail(int userId, string scope, string name, int sortOrder)
    {
        using var db = _db.NewContext(userId);
        var rail = new SavedFilter
        {
            Name = name,
            Scope = scope,
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
            new PageSection(HomeSections.RailKey(second)),
            new PageSection(HomeSections.RailKey(first)),
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

    [Fact]
    public async Task GetUi_returns_discover_layout_with_the_callers_discover_rails_before_trending()
    {
        var alice = _db.SeedUser("alice");
        var railId = SeedDiscoverRail(alice);

        var ui = Body(await Controller(alice).GetUi(CancellationToken.None));

        var keys = ui.DiscoverLayout!.Sections!.Select(s => s.Key).ToList();
        var railAt = keys.IndexOf(HomeSections.RailKey(railId));
        Assert.True(railAt >= 0);
        Assert.True(railAt < keys.IndexOf(DiscoverSections.Trending));
    }

    [Fact]
    public async Task Home_and_discover_rails_do_not_cross_into_each_others_layout()
    {
        var alice = _db.SeedUser("alice");
        var homeRail = SeedHomeRail(alice);
        var discoverRail = SeedDiscoverRail(alice);

        var ui = Body(await Controller(alice).GetUi(CancellationToken.None));

        Assert.Contains(ui.HomeLayout.Sections!, s => s.Key == HomeSections.RailKey(homeRail));
        Assert.DoesNotContain(ui.HomeLayout.Sections!, s => s.Key == HomeSections.RailKey(discoverRail));
        Assert.Contains(ui.DiscoverLayout!.Sections!, s => s.Key == HomeSections.RailKey(discoverRail));
        Assert.DoesNotContain(ui.DiscoverLayout!.Sections!, s => s.Key == HomeSections.RailKey(homeRail));
    }

    [Fact]
    public async Task SetUi_with_null_discover_layout_leaves_the_stored_discover_layout_untouched()
    {
        var alice = _db.SeedUser("alice");
        var requestedDiscover = new DiscoverLayoutSpec([new PageSection(DiscoverSections.Genres, Enabled: false)]);
        await Controller(alice).SetUi(
            new SettingsController.UiSettings("library", HomeLayoutSpec.Default, DiscoverLayout: requestedDiscover),
            CancellationToken.None);

        // A second write (e.g. from the plain Settings page, which never edits Discover) omits it.
        var written = Body(await Controller(alice).SetUi(
            new SettingsController.UiSettings("library", HomeLayoutSpec.Default, DiscoverLayout: null),
            CancellationToken.None));
        var reread = Body(await Controller(alice).GetUi(CancellationToken.None));

        Assert.False(written.DiscoverLayout!.Sections!.Single(s => s.Key == DiscoverSections.Genres).Enabled);
        Assert.False(reread.DiscoverLayout!.Sections!.Single(s => s.Key == DiscoverSections.Genres).Enabled);
    }

    [Fact]
    public async Task SetUi_persists_hero_flags_and_panel_order()
    {
        var alice = _db.SeedUser("alice");
        var requested = new SettingsController.UiSettings("library", new HomeLayoutSpec(true,
        [
            new PageSection(HomeSections.ContinueReading, Hero: false),
            new PageSection(HomeSections.Glance, Panels:
            [
                new PagePanel(HomeGlancePanels.ToRead),
                new PagePanel(HomeGlancePanels.Progress, Enabled: false),
                new PagePanel(HomeGlancePanels.Stats),
            ]),
        ]));

        await Controller(alice).SetUi(requested, CancellationToken.None);
        var reread = Body(await Controller(alice).GetUi(CancellationToken.None));

        Assert.False(reread.HomeLayout.Sections!.Single(s => s.Key == HomeSections.ContinueReading).Hero);
        var glance = reread.HomeLayout.Sections!.Single(s => s.Key == HomeSections.Glance);
        Assert.Equal(
            [HomeGlancePanels.ToRead, HomeGlancePanels.Progress, HomeGlancePanels.Stats],
            glance.Panels!.Select(p => p.Key));
        Assert.False(glance.Panels!.Single(p => p.Key == HomeGlancePanels.Progress).Enabled);
    }

    [Fact]
    public async Task GetUi_migrates_a_stored_legacy_home_blob_into_glance()
    {
        var alice = _db.SeedUser("alice");
        // What a pre-glance build stored: stats/continue/progress/toread as top-level sections.
        _db.SetUserConfig(alice, (SettingKeys.UiHomeSections,
            """{"enabled":true,"sections":[{"key":"stats","enabled":true},{"key":"continue","enabled":true}]}"""));

        var ui = Body(await Controller(alice).GetUi(CancellationToken.None));

        var keys = ui.HomeLayout.Sections!.Select(s => s.Key).ToList();
        Assert.DoesNotContain("stats", keys);
        Assert.Contains(HomeSections.Glance, keys);
        // The legacy key's own position (before continue) is where glance lands.
        Assert.True(keys.IndexOf(HomeSections.Glance) < keys.IndexOf(HomeSections.ContinueReading));

        var glance = ui.HomeLayout.Sections!.Single(s => s.Key == HomeSections.Glance);
        Assert.True(glance.Enabled);
        Assert.True(glance.Panels!.Single(p => p.Key == HomeGlancePanels.Stats).Enabled);
        // progress/toread were never in the legacy blob: appended as missing panels, on.
        Assert.True(glance.Panels!.Single(p => p.Key == HomeGlancePanels.Progress).Enabled);
        Assert.True(glance.Panels!.Single(p => p.Key == HomeGlancePanels.ToRead).Enabled);
    }
}
