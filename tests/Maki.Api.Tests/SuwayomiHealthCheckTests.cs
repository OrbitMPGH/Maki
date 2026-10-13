using System.Net;
using System.Text.Json;
using Maki.Api.Configuration;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Sources;
using Maki.Sources.Suwayomi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

[Collection(ConfigDirCollection.Name)]
public sealed class SuwayomiHealthCheckTests : IDisposable
{
    private readonly UpgradeWorld _world = new();
    private readonly FakeSuwayomiServer _server = new();
    private readonly SuwayomiClient _client;
    private readonly SourceRegistry _registry;

    public SuwayomiHealthCheckTests()
    {
        _world.Seed();
        var settings = new SuwayomiSettings(_world.Settings);
        SuwayomiTokenStore? tokens = null;
        var factory = new SuwayomiChainFactory(settings, () => tokens!, _server);
        tokens = new SuwayomiTokenStore(settings, factory);
        _client = new SuwayomiClient(factory, settings, TimeProvider.System);
        _registry = new SourceRegistry([],
            [new SuwayomiSourceProvider(settings, _client, new SuwayomiMangaIdCache(), NullLogger<SuwayomiSourceProvider>.Instance)]);
        _server.Sources(("100", "MangaDex", "MangaDex (EN)", "en"), ("200", "Bato.to", "Bato.to (EN)", "en"));
    }

    public void Dispose() => _world.Dispose();

    /// <summary>Stores these sources and switches them on, which a Suwayomi source never is by default.</summary>
    private async Task Expose(params string[] ids)
    {
        _world.Settings.Set(SettingKeys.SuwayomiUrl, "http://192.168.1.20:4567");
        await _world.Settings.SetAsync(SettingKeys.SuwayomiSources, ids.Length == 0 ? null : SuwayomiSettings.Serialize(
            ids.Select(id => new SuwayomiStoredSource(id, $"Source {id}", $"Source {id} (EN)", "en"))));
        await _world.Settings.SetAsync(SettingKeys.SourcePriorityOrder, string.Join(',', ids.Select(id => "suwayomi-" + id)));
        await _world.Settings.SetAsync(SettingKeys.SourcesDisabled, null);
        await _registry.ReloadAsync();
    }

    private async Task RefreshAsync()
    {
        using var db = _world.Db.NewContext();
        var services = new ServiceCollection().AddSingleton(_world.Queue).AddSingleton(_client).BuildServiceProvider();
        var monitor = new HealthMonitor(db, null!, _world.Settings, new AppPaths(), _registry,
            new SourceAvailability(_world.Settings, _registry), services, new RecordingNotifications(), _world.Inbox,
            new TestLocalizer(), new TestUserLocaleResolver(), null!);
        await monitor.RefreshAsync(CancellationToken.None);
    }

    private List<HealthCheckRecord> Rows()
    {
        using var db = _world.Db.NewContext();
        return [.. db.HealthChecks.Where(c => c.Id == "Suwayomi" || c.Id.StartsWith("suwayomi:")).OrderBy(c => c.Id)];
    }

    [Fact]
    public async Task Suwayomi_set_up_but_not_in_use_gets_no_row_and_no_call()
    {
        _world.Settings.Set(SettingKeys.SuwayomiUrl, "http://192.168.1.20:4567");

        await RefreshAsync();

        Assert.Empty(Rows());
        Assert.Empty(_server.Calls);
    }

    [Fact]
    public async Task An_uninstalled_extension_warns_then_recovers_then_leaves_with_its_source()
    {
        await Expose("100", "300");
        await RefreshAsync();

        Assert.Collection(Rows(),
            r =>
            {
                Assert.Equal("Suwayomi", r.Id);
                Assert.Equal("healthy", r.Status);
                Assert.Equal("/settings?tab=integrations&s=suwayomi", r.Url);
            },
            r =>
            {
                Assert.Equal("suwayomi:missing:300", r.Id);
                Assert.Equal("warning", r.Status);
                Assert.Equal("health.check.suwayomiSourceMissing", r.MessageKey);
                Assert.Equal("Source 300 (EN)", JsonDocument.Parse(r.ParamsJson!).RootElement.GetProperty("source").GetString());
            });

        _server.Sources(("100", "MangaDex", "MangaDex (EN)", "en"), ("300", "Back", "Source 300 (EN)", "en"));
        await RefreshAsync();
        var back = Assert.Single(Rows(), r => r.Id == "suwayomi:missing:300");
        Assert.Equal("healthy", back.Status);
        Assert.Equal("health.check.suwayomiSourceInstalled", back.MessageKey);

        await Expose("100");
        await RefreshAsync();
        Assert.Equal(["Suwayomi"], Rows().Select(r => r.Id));
    }

    [Fact]
    public async Task An_unreachable_server_is_an_error_and_says_nothing_about_missing_sources()
    {
        await Expose("100", "300");
        await RefreshAsync();
        Assert.Contains(Rows(), r => r.Id == "suwayomi:missing:300");

        _server.Ops["Sources"] = _ => new HttpResponseMessage(HttpStatusCode.BadGateway);
        await RefreshAsync();

        var rows = Rows();
        var connection = Assert.Single(rows, r => r.Id == "Suwayomi");
        Assert.Equal("error", connection.Status);
        Assert.Equal("health.check.connectionFailed", connection.MessageKey);
        Assert.Equal("warning", Assert.Single(rows, r => r.Id == "suwayomi:missing:300").Status);
    }

    [Fact]
    public async Task Switching_every_suwayomi_source_off_takes_the_rows_with_it()
    {
        await Expose("100", "300");
        await RefreshAsync();
        Assert.NotEmpty(Rows());

        await _world.Settings.SetAsync(SettingKeys.SourcePriorityOrder, "suwayomi-100,suwayomi-300");
        await _world.Settings.SetAsync(SettingKeys.SourcesDisabled, "suwayomi-100,suwayomi-300");
        var calls = _server.Calls.Count;
        await RefreshAsync();

        Assert.Empty(Rows());
        Assert.Equal(calls, _server.Calls.Count);
    }
}
