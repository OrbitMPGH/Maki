using System.Text.Json;
using Maki.Api.Configuration;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Maki.Api.Tests;

[Collection(ConfigDirCollection.Name)]
public sealed class IndexerHealthCheckTests : IDisposable
{
    private readonly UpgradeWorld _world = new();

    public IndexerHealthCheckTests() => _world.Seed();

    public void Dispose() => _world.Dispose();

    private async Task RefreshAsync()
    {
        using var db = _world.Db.NewContext();
        var services = new ServiceCollection()
            .AddSingleton(_world.Queue)
            .AddSingleton(TestIndexers.Registry(new StubHttpClientFactory("""<caps><server title="x" /></caps>""")))
            .BuildServiceProvider();
        var monitor = new HealthMonitor(db, null!, _world.Settings, new AppPaths(), _world.Registry,
            _world.Availability, services, new RecordingNotifications(), _world.Inbox,
            new TestLocalizer(), new TestUserLocaleResolver(), null!);
        await monitor.RefreshAsync(CancellationToken.None);
    }

    private HealthCheckRecord Row(string id)
    {
        using var db = _world.Db.NewContext();
        return db.HealthChecks.Single(c => c.Id == id);
    }

    [Theory]
    [InlineData("prowlarr", "Prowlarr")]
    [InlineData("jackett", "Jackett")]
    public async Task The_indexer_row_is_named_for_the_active_backend_and_links_to_its_card(string backend, string label)
    {
        _world.Settings.Set(SettingKeys.IndexerBackend, backend)
            .Set(SettingKeys.ProwlarrUrl, "http://prowlarr:9696").Set(SettingKeys.ProwlarrApiKey, "pkey")
            .Set(SettingKeys.JackettUrl, "http://jackett:9117").Set(SettingKeys.JackettApiKey, "jkey");

        await RefreshAsync();

        var row = Row("indexer");
        Assert.Equal("healthy", row.Status);
        Assert.Contains("s=indexer", row.Url);
        Assert.Equal(label, JsonDocument.Parse(row.ParamsJson!).RootElement.GetProperty("service").GetString());
    }

    [Fact]
    public async Task A_stored_legacy_prowlarr_row_is_removed()
    {
        using (var db = _world.Db.NewContext())
        {
            db.HealthChecks.Add(new HealthCheckRecord { Id = "Prowlarr", Category = "connections", Status = "healthy" });
            await db.SaveChangesAsync();
        }

        await RefreshAsync();

        using var check = _world.Db.NewContext();
        Assert.DoesNotContain(check.HealthChecks, c => c.Id == "Prowlarr");
        Assert.Contains(check.HealthChecks, c => c.Id == "indexer");
    }
}
