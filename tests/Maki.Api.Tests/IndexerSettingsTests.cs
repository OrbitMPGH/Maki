using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Download;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public sealed class IndexerSettingsTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private SettingsController Controller(SettingsService settings) => new(
        localizer: new TestLocalizer(), userLocales: new TestUserLocaleResolver(),
        settings: settings, naming: null!, flareSolverr: null!,
        indexers: TestIndexers.Registry(new StubHttpClientFactory("[]")),
        torrentClients: new TorrentClientRegistry([new FakeTorrentClient()]),
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

    private static T Body<T>(IActionResult result) => Assert.IsType<T>(Assert.IsType<OkObjectResult>(result).Value);

    [Fact]
    public async Task Get_reads_the_active_backend_unless_another_is_named()
    {
        _db.SetConfig(
            (SettingKeys.ProwlarrUrl, "http://prowlarr:9696"), (SettingKeys.ProwlarrApiKey, "pkey"),
            (SettingKeys.JackettUrl, "http://jackett:9117"), (SettingKeys.JackettApiKey, "jkey"));
        var controller = Controller(new SettingsService(_db.ScopeFactory()));

        Assert.Equal(new SettingsController.IndexerConnection("prowlarr", "http://prowlarr:9696", "pkey"),
            Body<SettingsController.IndexerConnection>(await controller.GetIndexer(null, default)));
        Assert.Equal(new SettingsController.IndexerConnection("jackett", "http://jackett:9117", "jkey"),
            Body<SettingsController.IndexerConnection>(await controller.GetIndexer("jackett", default)));
        Assert.IsNotType<OkObjectResult>(await controller.GetIndexer("torznab", default));
    }

    [Fact]
    public async Task Put_switches_backend_and_keeps_the_other_backends_values()
    {
        _db.SetConfig(
            (SettingKeys.ProwlarrUrl, "http://prowlarr:9696"), (SettingKeys.ProwlarrApiKey, "pkey"),
            (SettingKeys.ProwlarrIndexerIds, "1,2"));
        var settings = new SettingsService(_db.ScopeFactory());
        var controller = Controller(settings);

        var result = await controller.SetIndexer(new SettingsController.IndexerConnection("Jackett", "http://jackett:9117", "jkey"), default);

        Assert.Equal("jackett", Body<SettingsController.IndexerConnection>(result).Backend);
        Assert.Equal("jackett", await settings.GetAsync(SettingKeys.IndexerBackend));
        Assert.Equal("http://jackett:9117", await settings.GetAsync(SettingKeys.JackettUrl));
        Assert.Equal("http://prowlarr:9696", await settings.GetAsync(SettingKeys.ProwlarrUrl));
        Assert.Equal(new SettingsController.IndexerOptions(null, null),
            Body<SettingsController.IndexerOptions>(await controller.GetIndexerOptions(null, default)));

        await controller.SetIndexerOptions(new SettingsController.IndexerOptions("nyaasi", "7030"), default);
        Assert.Equal("nyaasi", await settings.GetAsync(SettingKeys.JackettIndexerIds));
        Assert.Equal("1,2", await settings.GetAsync(SettingKeys.ProwlarrIndexerIds));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nzbhydra")]
    public async Task Put_without_a_known_backend_changes_nothing(string? backend)
    {
        _db.SetConfig((SettingKeys.IndexerBackend, "jackett"));
        var settings = new SettingsService(_db.ScopeFactory());

        var result = await Controller(settings).SetIndexer(new SettingsController.IndexerConnection(backend, "http://x:1", "k"), default);

        Assert.Equal(400, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Equal("jackett", await settings.GetAsync(SettingKeys.IndexerBackend));
    }

    [Fact]
    public async Task Listing_indexers_of_an_unconfigured_backend_is_refused()
    {
        _db.SetConfig((SettingKeys.IndexerBackend, "jackett"), (SettingKeys.ProwlarrUrl, "http://prowlarr:9696"),
            (SettingKeys.ProwlarrApiKey, "pkey"));

        var result = await Controller(new SettingsService(_db.ScopeFactory())).GetIndexerList(null, default);

        Assert.Equal(400, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task Options_read_and_write_the_named_backend_without_touching_the_active_one()
    {
        _db.SetConfig(
            (SettingKeys.IndexerBackend, "jackett"),
            (SettingKeys.ProwlarrIndexerIds, "1,2"), (SettingKeys.ProwlarrCategories, "7030"),
            (SettingKeys.JackettIndexerIds, "nyaasi"));
        var settings = new SettingsService(_db.ScopeFactory());
        var controller = Controller(settings);

        Assert.Equal(new SettingsController.IndexerOptions("1,2", "7030"),
            Body<SettingsController.IndexerOptions>(await controller.GetIndexerOptions("prowlarr", default)));
        Assert.Equal("nyaasi",
            Body<SettingsController.IndexerOptions>(await controller.GetIndexerOptions("", default)).IndexerIds);

        await controller.SetIndexerOptions(new SettingsController.IndexerOptions("5", "8000", "prowlarr"), default);

        Assert.Equal("5", await settings.GetAsync(SettingKeys.ProwlarrIndexerIds));
        Assert.Equal("8000", await settings.GetAsync(SettingKeys.ProwlarrCategories));
        Assert.Equal("nyaasi", await settings.GetAsync(SettingKeys.JackettIndexerIds));
    }

    [Fact]
    public async Task An_unknown_backend_is_refused_by_the_options_and_list_routes()
    {
        var controller = Controller(new SettingsService(_db.ScopeFactory()));

        Assert.Equal(400, Assert.IsAssignableFrom<ObjectResult>(await controller.GetIndexerOptions("torznab", default)).StatusCode);
        Assert.Equal(400, Assert.IsAssignableFrom<ObjectResult>(
            await controller.SetIndexerOptions(new SettingsController.IndexerOptions("1", null, "torznab"), default)).StatusCode);
        Assert.Equal(400, Assert.IsAssignableFrom<ObjectResult>(await controller.GetIndexerList("torznab", default)).StatusCode);
    }

    [Fact]
    public async Task The_list_of_a_non_active_backend_uses_that_backends_connection()
    {
        _db.SetConfig((SettingKeys.IndexerBackend, "jackett"), (SettingKeys.ProwlarrUrl, "http://prowlarr:9696"),
            (SettingKeys.ProwlarrApiKey, "pkey"));

        var result = await Controller(new SettingsService(_db.ScopeFactory())).GetIndexerList("prowlarr", default);

        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<Maki.Core.Indexers.IndexerInfo>>(Assert.IsType<OkObjectResult>(result).Value));
    }
}
