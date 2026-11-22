using Maki.Api.Controllers;
using Maki.Core.Configuration;
using Maki.Core.Http.Proxy;
using Maki.Core.Sources;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>The source proxy setting: what a save accepts, and what it keeps that the admin never saw.</summary>
public sealed class SourceProxySettingsTests : IDisposable
{
    private readonly TestDb _db;
    private readonly Maki.Api.Services.SettingsService _settings;

    public SourceProxySettingsTests()
    {
        _db = new TestDb();
        _settings = new Maki.Api.Services.SettingsService(_db.ScopeFactory());
    }

    public void Dispose() => _db.Dispose();

    private SettingsController Controller(params string[] sources) => new(
        localizer: new TestLocalizer(), userLocales: new TestUserLocaleResolver(),
        settings: _settings, naming: null!, flareSolverr: null!, indexers: null!, torrentClients: null!,
        kavita: null!, sourceRegistry: new SourceRegistry(sources.Select(n => (ISource)new FakeSource { Name = n })),
        sourceAvailability: null!,
        mangaBakaDump: null!, embeddingModel: null!, embeddingStore: null!, embeddingStatus: null!,
        embeddingIndexer: null!, prebuiltIndex: null!, recoGraph: null!,
        recoGraphCache: null!, coReadInstaller: null!, coReadCache: null!, easeInstaller: null!, easeCache: null!,
        readerCohortInstaller: null!,
        readerCohortCache: null!, tasteVectorInstaller: null!, vectorIndexCache: null!,
        modelSwitcher: null!, db: _db.NewContext(), updateCheck: null!, currentUser: new TestCurrentUser(1),
        userSettings: null!, kavitaUser: null!, kavitaLive: null!, schedulerFactory: null!, scopeFactory: _db.ScopeFactory(),
        logger: NullLogger<SettingsController>.Instance);

    private static SettingsController.SourceProxySettings Saved(IActionResult result) =>
        Assert.IsType<SettingsController.SourceProxySettings>(Assert.IsType<OkObjectResult>(result).Value);

    private static string? Code(IActionResult result) =>
        Assert.IsAssignableFrom<ObjectResult>(result).Value?.GetType().GetProperty("code")?.GetValue(((ObjectResult)result).Value) as string;

    [Fact]
    public async Task A_save_normalises_the_address_and_reads_back()
    {
        var controller = Controller("mangafire", "mangadex");

        var saved = Saved(await controller.SetSourceProxy(new(" http://192.168.1.20:3128/ ", ["MangaFire"]), default));

        Assert.Equal("http://192.168.1.20:3128", saved.Url);
        Assert.Equal(["MangaFire"], saved.Sources);
        var route = await new SourceProxyResolver(_settings).ResolveAsync("mangafire");
        Assert.Equal("http://192.168.1.20:3128", route.Fingerprint);
        Assert.True((await new SourceProxyResolver(_settings).ResolveAsync("mangadex")).IsDirect);
    }

    [Theory]
    [InlineData("socks5://192.168.1.20:1080", "error.settings.sourceProxyHttpOnly")]
    [InlineData("http://user:pass@192.168.1.20:3128", "error.settings.sourceProxyNoCredentials")]
    [InlineData("http://192.168.1.20", "error.settings.sourceProxyInvalidUrl")]
    public async Task A_proxy_v1_cannot_use_is_refused_and_nothing_is_written(string url, string code)
    {
        var result = await Controller("mangafire").SetSourceProxy(new(url, ["mangafire"]), default);

        Assert.Equal(code, Code(result));
        Assert.Null(await _settings.GetAsync(SettingKeys.SourceProxy));
    }

    [Fact]
    public async Task Unknown_and_suwayomi_sources_are_refused()
    {
        var controller = Controller("mangafire", "suwayomi-1234");

        Assert.Equal("error.settings.unknownSources", Code(await controller.SetSourceProxy(new("http://10.0.0.2:8888", ["nope"]), default)));
        Assert.Equal("error.settings.unknownSources", Code(await controller.SetSourceProxy(new("http://10.0.0.2:8888", ["suwayomi-1234"]), default)));
    }

    [Fact]
    public async Task A_source_not_registered_right_now_keeps_its_assignment()
    {
        await Controller("mangafire", "custom-gone").SetSourceProxy(new("http://10.0.0.2:8888", ["mangafire", "custom-gone"]), default);

        // The custom source was removed; the admin saves with what they can see.
        var saved = Saved(await Controller("mangafire").SetSourceProxy(new("http://10.0.0.3:8888", []), default));

        Assert.Equal(["custom-gone"], saved.Sources);
        Assert.Equal("http://10.0.0.3:8888", (await new SourceProxyResolver(_settings).ResolveAsync("custom-gone")).Fingerprint);
    }

    [Fact]
    public async Task Clearing_the_address_while_sources_use_it_is_refused()
    {
        var controller = Controller("mangafire");
        await controller.SetSourceProxy(new("http://10.0.0.2:8888", ["mangafire"]), default);

        Assert.Equal("error.settings.sourceProxyInUse", Code(await controller.SetSourceProxy(new("", ["mangafire"]), default)));

        Assert.Null(Saved(await controller.SetSourceProxy(new("", []), default)).Url);
        Assert.Null(await _settings.GetAsync(SettingKeys.SourceProxy));
    }
}
