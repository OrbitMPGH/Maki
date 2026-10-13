using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public sealed class KavitaSettingsTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private SettingsController Controller(SettingsService settings) => new(
        localizer: new TestLocalizer(), userLocales: new TestUserLocaleResolver(),
        settings: settings, naming: null!, flareSolverr: null!, prowlarr: null!, qbittorrent: null!,
        kavita: null!, sourceRegistry: null!, sourceAvailability: null!,
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
    public async Task Saving_the_connection_leaves_the_attributed_user_alone()
    {
        _db.SetConfig((SettingKeys.KavitaUserId, "7"));
        var settings = new SettingsService(_db.ScopeFactory());

        var result = await Controller(settings).SetKavita(
            new SettingsController.KavitaSettings("http://kavita:5000", "key", "/data/manga", "/manga"), default);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("7", await settings.GetAsync(SettingKeys.KavitaUserId));
        Assert.Equal("http://kavita:5000", await settings.GetAsync(SettingKeys.KavitaUrl));
    }
}
