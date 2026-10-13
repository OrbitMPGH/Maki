using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Sources;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public sealed class SourcePriorityPreservationTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private SettingsController Controller(SettingsService settings, SourceRegistry registry) => new(
        localizer: new TestLocalizer(), userLocales: new TestUserLocaleResolver(),
        settings: settings, naming: null!, flareSolverr: null!, prowlarr: null!, qbittorrent: null!,
        kavita: null!, sourceRegistry: registry, sourceAvailability: new SourceAvailability(settings, registry),
        mangaBakaDump: null!, embeddingModel: null!, embeddingStore: null!, embeddingStatus: null!,
        embeddingIndexer: null!, prebuiltIndex: null!, recoGraph: null!,
        recoGraphCache: null!, coReadInstaller: null!, coReadCache: null!, readerCohortInstaller: null!,
        readerCohortCache: null!, tasteVectorInstaller: null!, vectorIndexCache: null!,
        modelSwitcher: null!, db: _db.NewContext(), updateCheck: null!, currentUser: new TestCurrentUser(1),
        userSettings: null!,
        kavitaUser: new KavitaUserResolver(_db.ScopeFactory(), settings),
        kavitaLive: new KavitaLiveReadSync(settings, null!, null!, null!, null!, null!,
            NullLogger<KavitaLiveReadSync>.Instance),
        schedulerFactory: null!, scopeFactory: null!,
        logger: NullLogger<SettingsController>.Instance);

    [Fact]
    public async Task Saving_keeps_choices_for_sources_that_are_not_registered_right_now()
    {
        _db.SetConfig(
            (SettingKeys.SourcePriorityOrder, "alpha,suwayomi-1,suwayomi-2"),
            (SettingKeys.SourcesDisabled, "suwayomi-2"));
        var settings = new SettingsService(_db.ScopeFactory());
        var registry = new SourceRegistry([new FakeSource { Name = "alpha" }, new FakeSource { Name = "beta" }]);

        var result = await Controller(settings, registry).SetSourcePriority(
            new SettingsController.SourcePrioritySettings(["beta", "alpha"], ["beta"]), default);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("beta,alpha,suwayomi-1,suwayomi-2", await settings.GetAsync(SettingKeys.SourcePriorityOrder));
        Assert.Equal("beta,suwayomi-2", await settings.GetAsync(SettingKeys.SourcesDisabled));
    }

    [Fact]
    public async Task Saving_still_rejects_names_nobody_has_stored()
    {
        _db.SetConfig((SettingKeys.SourcePriorityOrder, "alpha,suwayomi-1"));
        var settings = new SettingsService(_db.ScopeFactory());
        var registry = new SourceRegistry([new FakeSource { Name = "alpha" }]);

        var result = await Controller(settings, registry).SetSourcePriority(
            new SettingsController.SourcePrioritySettings(["alpha", "suwayomi-1"], []), default);

        Assert.IsNotType<OkObjectResult>(result);
        Assert.Equal("alpha,suwayomi-1", await settings.GetAsync(SettingKeys.SourcePriorityOrder));
    }
}
