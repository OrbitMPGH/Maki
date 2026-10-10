using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Download;
using Maki.Core.Entities;
using Maki.Core.Indexers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// <see cref="ReleaseService"/>: config-getter guards, Prowlarr search (torrent filter + seeder
/// ordering) over a stubbed HTTP client, and grab (magnet-hash extraction + queue persistence)
/// over a fake qBittorrent client.
/// </summary>
public class ReleaseServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly SettingsService _settings;
    private readonly RecordingQbt _qbt = new();

    public ReleaseServiceTests() => _settings = new SettingsService(_db.ScopeFactory());

    public void Dispose() => _db.Dispose();

    private sealed class RecordingQbt : QBittorrentClient
    {
        public string? AddedLink { get; private set; }

        public override Task AddAsync(
            string baseUrl, string username, string password,
            string urlOrMagnet, string category, CancellationToken ct = default)
        {
            AddedLink = urlOrMagnet;
            return Task.CompletedTask;
        }
    }

    private ReleaseService Build(string prowlarrJson = "[]")
    {
        var prowlarr = new ProwlarrClient(new StubHttpClientFactory(prowlarrJson));
        return new ReleaseService(_db.NewContext(), prowlarr, _qbt, _settings, NullLogger<ReleaseService>.Instance);
    }

    private async Task ConfigureProwlarr()
    {
        await _settings.SetAsync(SettingKeys.ProwlarrUrl, "http://prowlarr.test");
        await _settings.SetAsync(SettingKeys.ProwlarrApiKey, "key");
    }

    private static string Release(string guid, string protocol, int? seeders, string? magnet = null, string? download = null)
    {
        string JsonStr(string? s) => s is null ? "null" : $"\"{s}\"";
        return $"{{\"guid\":\"{guid}\",\"title\":\"{guid} title\",\"size\":1000,\"indexerId\":1,"
             + $"\"indexer\":\"Nyaa\",\"seeders\":{seeders?.ToString() ?? "null"},\"leechers\":0,"
             + $"\"downloadUrl\":{JsonStr(download)},\"magnetUrl\":{JsonStr(magnet)},"
             + $"\"protocol\":\"{protocol}\",\"infoUrl\":null,\"ageMinutes\":10}}";
    }

    [Fact]
    public async Task Prowlarr_config_throws_until_both_url_and_key_are_set()
    {
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Build().GetProwlarrConfigAsync(CancellationToken.None));

        await _settings.SetAsync(SettingKeys.ProwlarrUrl, "http://prowlarr.test");
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Build().GetProwlarrConfigAsync(CancellationToken.None));

        await _settings.SetAsync(SettingKeys.ProwlarrApiKey, "key");
        var (url, apiKey) = await Build().GetProwlarrConfigAsync(CancellationToken.None);
        Assert.Equal("http://prowlarr.test", url);
        Assert.Equal("key", apiKey);
    }

    [Fact]
    public async Task Qbt_config_throws_without_url_then_fills_defaults()
    {
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Build().GetQbtConfigAsync(CancellationToken.None));

        await _settings.SetAsync(SettingKeys.QBittorrentUrl, "http://qbt.test");
        var config = await Build().GetQbtConfigAsync(CancellationToken.None);

        Assert.Equal("http://qbt.test", config.Url);
        Assert.Equal(string.Empty, config.Username);
        Assert.Equal("maki", config.Category);
    }

    [Fact]
    public async Task Qbt_path_map_defaults_to_null_and_reflects_settings()
    {
        Assert.Equal((null, null), await Build().GetQbtPathMapAsync(CancellationToken.None));

        await _settings.SetAsync(SettingKeys.QBittorrentPathMapFrom, "/downloads");
        await _settings.SetAsync(SettingKeys.QBittorrentPathMapTo, @"Z:\downloads");

        var (from, to) = await Build().GetQbtPathMapAsync(CancellationToken.None);
        Assert.Equal("/downloads", from);
        Assert.Equal(@"Z:\downloads", to);
    }

    [Fact]
    public async Task Search_returns_only_torrents_ordered_by_seeders()
    {
        var seriesId = _db.SeedSeries("Berserk");
        await ConfigureProwlarr();
        var json = "[" + string.Join(",",
            Release("low", "torrent", 5),
            Release("usenet", "usenet", 999),
            Release("high", "torrent", 50)) + "]";

        var result = await Build(json).SearchAsync(seriesId, "berserk");

        Assert.Equal(["high", "low"], result.Releases.Select(r => r.Guid));
        Assert.All(result.Releases, r => Assert.Equal("torrent", r.Protocol));
    }

    [Fact]
    public async Task Search_with_no_torrents_returns_empty()
    {
        var seriesId = _db.SeedSeries("Berserk");
        await ConfigureProwlarr();

        var result = await Build("[" + Release("u", "usenet", 3) + "]").SearchAsync(seriesId, "berserk");

        Assert.Empty(result.Releases);
        Assert.Equal("berserk", result.Query);
    }

    [Fact]
    public async Task Search_without_a_query_retries_alternate_titles_up_to_the_cap()
    {
        var seriesId = _db.SeedSeries("Main Title");
        using (var db = _db.NewContext())
        {
            var series = db.Series.First(s => s.Id == seriesId);
            series.OriginalTitle = "Original";
            series.AltTitles = [.. Enumerable.Range(1, 6).Select(i => new LocalizedTitle($"Alt {i}", "en"))];
            db.SaveChanges();
        }

        await ConfigureProwlarr();

        var result = await Build().SearchAsync(seriesId);

        Assert.Empty(result.Releases);
        Assert.Equal("Alt 3", result.Query);
    }

    [Fact]
    public async Task Search_with_an_explicit_query_does_not_fall_back()
    {
        var seriesId = _db.SeedSeries("Main Title");
        using (var db = _db.NewContext())
        {
            db.Series.First(s => s.Id == seriesId).AltTitles = [new LocalizedTitle("Alt One", "en")];
            db.SaveChanges();
        }

        await ConfigureProwlarr();

        var result = await Build().SearchAsync(seriesId, "typed");

        Assert.Equal("typed", result.Query);
    }

    [Fact]
    public async Task Grab_extracts_the_magnet_infohash_and_persists_a_torrent_item()
    {
        var seriesId = _db.SeedSeries("Berserk");
        await _settings.SetAsync(SettingKeys.QBittorrentUrl, "http://qbt.test");
        var release = new ReleaseDto(
            "g", "Berserk v01", 1000, "Nyaa", 10, 1, "torrent",
            DownloadUrl: null,
            MagnetUrl: "magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567&dn=x",
            InfoUrl: null);

        var item = await Build().GrabAsync(seriesId, release, DownloadOrigin.Manual, null, null);

        Assert.Equal(AcquisitionProtocol.Torrent, item.Protocol);
        Assert.Equal(QueueStatus.Downloading, item.Status);
        Assert.Equal(release.MagnetUrl, _qbt.AddedLink);
        Assert.Contains("0123456789abcdef0123456789abcdef01234567", item.ReleaseInfoJson);

        using var db = _db.NewContext();
        Assert.Equal(1, db.DownloadQueue.Count(q => q.SeriesId == seriesId));
    }

    [Fact]
    public async Task Grab_endpoint_sends_the_link_the_search_returned_not_the_one_in_the_request()
    {
        var seriesId = _db.SeedSeries("Berserk");
        await ConfigureProwlarr();
        await _settings.SetAsync(SettingKeys.QBittorrentUrl, "http://qbt.test");
        const string magnet = "magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567";
        var service = Build("[" + Release("g", "torrent", 5, magnet: magnet) + "]");
        var controller = new ReleaseController(
            service, new ReleaseSearchCache(new MemoryCache(new MemoryCacheOptions())), new TestLocalizer(),
            NullLogger<ReleaseController>.Instance);
        var forged = new ReleaseDto("g", "Forged", 1, "Forged", 0, 0, "torrent",
            DownloadUrl: "http://169.254.169.254/latest/meta-data", MagnetUrl: null, InfoUrl: null);

        using var db = _db.NewContext();
        var torrents = new TorrentUpgradeService(db, new UpgradeEvaluationService(db, TestQuality.Create()), service,
            null!, new FakeAppSettings(), TimeProvider.System, NullLogger<TorrentUpgradeService>.Instance);
        Assert.IsType<OkObjectResult>(await controller.Search(seriesId, "berserk", torrents, CancellationToken.None));
        var result = await controller.Grab(
            new ReleaseController.GrabRequest(seriesId, forged), new TestCurrentUser(1), db, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(magnet, _qbt.AddedLink);
        Assert.Equal("g title", db.DownloadQueue.Single(q => q.SeriesId == seriesId).Title);
    }

    [Fact]
    public async Task Grab_endpoint_refuses_a_release_no_search_returned()
    {
        var seriesId = _db.SeedSeries("Berserk");
        await _settings.SetAsync(SettingKeys.QBittorrentUrl, "http://qbt.test");
        var forged = new ReleaseDto("g", "Forged", 1, "Forged", 0, 0, "torrent",
            DownloadUrl: "http://169.254.169.254/latest/meta-data", MagnetUrl: null, InfoUrl: null);

        using var db = _db.NewContext();
        var result = await new ReleaseController(
                Build(), new ReleaseSearchCache(new MemoryCache(new MemoryCacheOptions())), new TestLocalizer(),
                NullLogger<ReleaseController>.Instance)
            .Grab(new ReleaseController.GrabRequest(seriesId, forged), new TestCurrentUser(1), db, CancellationToken.None);

        Assert.Equal(400, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Null(_qbt.AddedLink);
        Assert.Empty(db.DownloadQueue);
    }

    [Fact]
    public async Task Grab_records_the_origin_the_user_and_the_upgrade_info()
    {
        var seriesId = _db.SeedSeries("Berserk");
        await _settings.SetAsync(SettingKeys.QBittorrentUrl, "http://qbt.test");
        var release = new ReleaseDto("g", "Berserk v01", 1000, "Nyaa", 10, 1, "torrent", "http://x/t.torrent", null, null);

        var manual = await Build().GrabAsync(seriesId, release, DownloadOrigin.Manual, 7, null);
        var upgrade = await Build().GrabAsync(seriesId, release, DownloadOrigin.Upgrade, null, "{\"kind\":\"torrent\"}");

        using var db = _db.NewContext();
        var manualRow = db.DownloadQueue.Single(q => q.Id == manual.Id);
        Assert.Equal(DownloadOrigin.Manual, manualRow.Origin);
        Assert.Equal(7, manualRow.QueuedByUserId);
        Assert.Null(manualRow.UpgradeInfoJson);
        var upgradeRow = db.DownloadQueue.Single(q => q.Id == upgrade.Id);
        Assert.Equal(DownloadOrigin.Upgrade, upgradeRow.Origin);
        Assert.Null(upgradeRow.QueuedByUserId);
        Assert.Equal("{\"kind\":\"torrent\"}", upgradeRow.UpgradeInfoJson);
    }

    [Fact]
    public async Task Grab_without_a_download_link_throws()
    {
        var seriesId = _db.SeedSeries("Berserk");
        await _settings.SetAsync(SettingKeys.QBittorrentUrl, "http://qbt.test");
        var release = new ReleaseDto("g", "t", 1, "Nyaa", 1, 0, "torrent", null, null, null);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Build().GrabAsync(seriesId, release, DownloadOrigin.Manual, null, null));
    }

    [Fact]
    public async Task Grab_for_a_missing_series_throws()
    {
        await _settings.SetAsync(SettingKeys.QBittorrentUrl, "http://qbt.test");
        var release = new ReleaseDto("g", "t", 1, "Nyaa", 1, 0, "torrent", "http://x/t.torrent", null, null);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Build().GrabAsync(404, release, DownloadOrigin.Manual, null, null));
    }
}
