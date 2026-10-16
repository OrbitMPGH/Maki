using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Download;
using Maki.Core.Entities;
using Maki.Core.Indexers;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// <see cref="ReleaseService"/>: config-getter guards, indexer search (torrent filter + seeder
/// ordering) over a stubbed HTTP client, and grab (magnet-hash extraction + queue persistence)
/// over a fake download client.
/// </summary>
public class ReleaseServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly SettingsService _settings;
    private readonly FakeTorrentClient _client = new();

    public ReleaseServiceTests() => _settings = new SettingsService(_db.ScopeFactory());

    public void Dispose() => _db.Dispose();

    /// <summary>Serves a release's download link: a body, or a redirect to <c>Location</c>.</summary>
    private sealed class DownloadLinkFactory(byte[] body, string? location = null) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(body, location));

        private sealed class Handler(byte[] body, string? location) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var response = location is null
                    ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                    : new HttpResponseMessage(System.Net.HttpStatusCode.MovedPermanently) { Headers = { Location = new Uri(location) } };
                return Task.FromResult(response);
            }
        }
    }

    private ReleaseService Build(string indexerJson = "[]") => Build(new StubHttpClientFactory(indexerJson));

    private ReleaseService Build(IHttpClientFactory indexerHttp) =>
        new(_db.NewContext(), TestIndexers.Registry(indexerHttp), _client.Registry(), _settings, NullLogger<ReleaseService>.Instance);

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
    public async Task Indexer_config_throws_until_both_url_and_key_are_set()
    {
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Build().GetIndexerSettingsAsync(CancellationToken.None));

        await _settings.SetAsync(SettingKeys.ProwlarrUrl, "http://prowlarr.test");
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Build().GetIndexerSettingsAsync(CancellationToken.None));

        await _settings.SetAsync(SettingKeys.ProwlarrApiKey, "key");
        var config = await Build().GetIndexerSettingsAsync(CancellationToken.None);
        Assert.Equal(IndexerBackend.Prowlarr, config.Backend);
        Assert.Equal("http://prowlarr.test", config.Url);
        Assert.Equal("key", config.ApiKey);
    }

    [Fact]
    public async Task Indexer_config_reads_the_active_backends_own_keys()
    {
        await ConfigureProwlarr();
        await _settings.SetAsync(SettingKeys.ProwlarrIndexerIds, "1,2");
        await _settings.SetAsync(SettingKeys.IndexerBackend, "jackett");
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Build().GetIndexerSettingsAsync(CancellationToken.None));

        await _settings.SetAsync(SettingKeys.JackettUrl, "http://jackett.test:9117");
        await _settings.SetAsync(SettingKeys.JackettApiKey, "jkey");
        await _settings.SetAsync(SettingKeys.JackettIndexerIds, "nyaasi, animebytes");
        await _settings.SetAsync(SettingKeys.JackettCategories, "7030,x,8000");
        var config = await Build().GetIndexerSettingsAsync(CancellationToken.None);

        Assert.Equal(IndexerBackend.Jackett, config.Backend);
        Assert.Equal("http://jackett.test:9117", config.Url);
        Assert.Equal(["nyaasi", "animebytes"], config.IndexerIds);
        Assert.Equal([7030, 8000], config.Categories);
    }

    [Fact]
    public async Task Search_goes_to_jackett_when_it_is_the_active_backend()
    {
        var seriesId = _db.SeedSeries("Berserk");
        await _settings.SetAsync(SettingKeys.IndexerBackend, "jackett");
        await _settings.SetAsync(SettingKeys.JackettUrl, "http://jackett.test:9117");
        await _settings.SetAsync(SettingKeys.JackettApiKey, "jkey");
        var http = new StubHttpClientFactory("""
            {"Results":[{"Title":"Berserk v01","Guid":"http://nyaa/1","Link":"http://jackett.test:9117/dl/nyaasi/?jackett_apikey=jkey&path=x",
              "Size":1000,"Seeders":7,"Peers":10,"InfoHash":"0123456789ABCDEF0123456789ABCDEF01234567","Tracker":"Nyaa.si"}],
             "Indexers":[{"ID":"nyaasi","Name":"Nyaa.si","Status":2,"Results":1,"Error":null}]}
            """);

        var result = await Build(http).SearchAsync(seriesId, "berserk");

        var release = Assert.Single(result.Releases);
        Assert.Equal(IndexerBackend.Jackett, result.Backend);
        Assert.Equal("Nyaa.si", release.Indexer);
        Assert.Equal(3, release.Leechers);
        Assert.Equal("0123456789ABCDEF0123456789ABCDEF01234567", release.InfoHash);
        Assert.StartsWith("http://jackett.test:9117/api/v2.0/indexers/all/results?", http.LastRequestUri);
    }

    [Fact]
    public async Task Client_settings_throw_without_url_then_fill_defaults()
    {
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Build().GetClientSettingsAsync(CancellationToken.None));

        await _settings.SetAsync(SettingKeys.DownloadClientUrl, "http://qbt.test");
        var config = await Build().GetClientSettingsAsync(CancellationToken.None);

        Assert.Equal(TorrentClientKind.QBittorrent, config.Kind);
        Assert.Equal("http://qbt.test", config.Url);
        Assert.Equal(string.Empty, config.Username);
        Assert.Equal("maki", config.Label);
    }

    [Theory]
    [InlineData("transmission", TorrentClientKind.Transmission)]
    [InlineData("Deluge", TorrentClientKind.Deluge)]
    [InlineData("utorrent", TorrentClientKind.QBittorrent)]
    public async Task Client_settings_read_the_stored_kind_and_fall_back_to_qbittorrent(string stored, TorrentClientKind expected)
    {
        await _settings.SetAsync(SettingKeys.DownloadClientUrl, "http://client.test");
        await _settings.SetAsync(SettingKeys.DownloadClientKind, stored);

        Assert.Equal(expected, (await Build().GetClientSettingsAsync(CancellationToken.None)).Kind);
    }

    [Fact]
    public async Task Client_path_map_defaults_to_null_and_reflects_settings()
    {
        Assert.Equal((null, null), await Build().GetClientPathMapAsync(CancellationToken.None));

        await _settings.SetAsync(SettingKeys.DownloadClientPathMapFrom, "/downloads");
        await _settings.SetAsync(SettingKeys.DownloadClientPathMapTo, @"Z:\downloads");

        var (from, to) = await Build().GetClientPathMapAsync(CancellationToken.None);
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
        await _settings.SetAsync(SettingKeys.DownloadClientUrl, "http://qbt.test");
        var release = new ReleaseDto(
            "g", "Berserk v01", 1000, "Nyaa", 10, 1, "torrent",
            DownloadUrl: null,
            MagnetUrl: "magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567&dn=x",
            InfoUrl: null);

        var item = await Build().GrabAsync(seriesId, release, DownloadOrigin.Manual, null, null);

        Assert.Equal(AcquisitionProtocol.Torrent, item.Protocol);
        Assert.Equal(QueueStatus.Downloading, item.Status);
        Assert.Equal([release.MagnetUrl!], _client.AddedLinks);
        Assert.Contains("0123456789abcdef0123456789abcdef01234567", item.ReleaseInfoJson);

        using var db = _db.NewContext();
        Assert.Equal(1, db.DownloadQueue.Count(q => q.SeriesId == seriesId));
    }

    [Fact]
    public async Task Grab_uploads_a_torrent_file_and_records_its_infohash()
    {
        var seriesId = _db.SeedSeries("Berserk");
        await _settings.SetAsync(SettingKeys.DownloadClientUrl, "http://qbt.test");
        var torrent = System.Text.Encoding.ASCII.GetBytes(
            "d4:infod6:lengthi12345e4:name8:test.cbz12:piece lengthi16384e6:pieces20:aaaaaaaaaaaaaaaaaaaaee");
        var release = new ReleaseDto("g", "Berserk v01", 1000, "Nyaa", 10, 1, "torrent", "http://prowlarr.test/1/download", null, null);

        var item = await Build(new DownloadLinkFactory(torrent)).GrabAsync(seriesId, release, DownloadOrigin.Manual, null, null);

        Assert.Empty(_client.AddedLinks);
        var added = Assert.Single(_client.AddedFiles);
        Assert.Equal(torrent, added.File);
        Assert.Equal("b0d01790a719342ff42de4fd924dea04a8286157", added.Hash);
        Assert.Equal("b0d01790a719342ff42de4fd924dea04a8286157",
            System.Text.Json.JsonSerializer.Deserialize<ReleaseInfo>(item.ReleaseInfoJson!)!.TorrentHash);
    }

    [Fact]
    public async Task Grab_follows_a_download_link_that_redirects_to_a_magnet()
    {
        var seriesId = _db.SeedSeries("Berserk");
        await _settings.SetAsync(SettingKeys.DownloadClientUrl, "http://qbt.test");
        const string magnet = "magnet:?xt=urn:btih:AERUKZ4JVPG66AJDIVTYTK6N54ASGRLH&dn=x";
        var release = new ReleaseDto("g", "Berserk v01", 1000, "Nyaa", 10, 1, "torrent", "http://prowlarr.test/1/download", null, null);

        var item = await Build(new DownloadLinkFactory([], magnet)).GrabAsync(seriesId, release, DownloadOrigin.Manual, null, null);

        Assert.Equal([magnet], _client.AddedLinks);
        Assert.Equal("0123456789abcdef0123456789abcdef01234567",
            System.Text.Json.JsonSerializer.Deserialize<ReleaseInfo>(item.ReleaseInfoJson!)!.TorrentHash);
    }

    [Fact]
    public async Task Grab_hands_the_client_the_link_when_it_does_not_serve_a_torrent_file()
    {
        var seriesId = _db.SeedSeries("Berserk");
        await _settings.SetAsync(SettingKeys.DownloadClientUrl, "http://qbt.test");
        var release = new ReleaseDto("g", "Berserk v01", 1000, "Nyaa", 10, 1, "torrent", "http://prowlarr.test/1/download", null, null);

        var item = await Build(new DownloadLinkFactory("<html>busy</html>"u8.ToArray()))
            .GrabAsync(seriesId, release, DownloadOrigin.Manual, null, null);

        Assert.Equal([release.DownloadUrl!], _client.AddedLinks);
        Assert.Empty(_client.AddedFiles);
        Assert.Null(System.Text.Json.JsonSerializer.Deserialize<ReleaseInfo>(item.ReleaseInfoJson!)!.TorrentHash);
    }

    [Fact]
    public async Task Grab_keeps_the_hash_a_client_reports_for_a_url_add()
    {
        var seriesId = _db.SeedSeries("Berserk");
        await _settings.SetAsync(SettingKeys.DownloadClientUrl, "http://client.test");
        _client.HashToReturn = "fedcba9876543210fedcba9876543210fedcba98";
        var release = new ReleaseDto("g", "Berserk v01", 1000, "Nyaa", 10, 1, "torrent", "http://prowlarr.test/1/download", null, null);

        var item = await Build(new DownloadLinkFactory("<html>busy</html>"u8.ToArray()))
            .GrabAsync(seriesId, release, DownloadOrigin.Manual, null, null);

        Assert.Equal([release.DownloadUrl!], _client.AddedLinks);
        Assert.Equal("fedcba9876543210fedcba9876543210fedcba98",
            System.Text.Json.JsonSerializer.Deserialize<ReleaseInfo>(item.ReleaseInfoJson!)!.TorrentHash);
    }

    [Fact]
    public async Task Grab_endpoint_sends_the_link_the_search_returned()
    {
        var seriesId = _db.SeedSeries("Berserk");
        await ConfigureProwlarr();
        await _settings.SetAsync(SettingKeys.DownloadClientUrl, "http://qbt.test");
        const string magnet = "magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567";
        var service = Build("[" + Release("g", "torrent", 5, magnet: magnet) + "]");
        var controller = new ReleaseController(
            service, new ReleaseSearchCache(new MemoryCache(new MemoryCacheOptions())), new TestLocalizer(),
            NullLogger<ReleaseController>.Instance);
        var forged = new ReleaseController.GrabRelease("g");

        using var db = _db.NewContext();
        var torrents = new TorrentUpgradeService(db, new UpgradeEvaluationService(db, TestQuality.Create()), service,
            null!, new FakeAppSettings(), TimeProvider.System, new FakeRelatedTitles(), NullLogger<TorrentUpgradeService>.Instance);
        Assert.IsType<OkObjectResult>(await controller.Search(seriesId, "berserk", torrents, CancellationToken.None));
        var result = await controller.Grab(
            new ReleaseController.GrabRequest(seriesId, forged), new TestCurrentUser(1), db, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal([magnet], _client.AddedLinks);
        Assert.Equal("g title", db.DownloadQueue.Single(q => q.SeriesId == seriesId).Title);
    }

    /// <summary>Answers the search with <c>searchJson</c> and every other request with a torrent file, recording the addresses.</summary>
    private sealed class RoutedFactory(string searchJson, byte[] torrent) : IHttpClientFactory
    {
        public string SearchJson { get; } = searchJson;

        public byte[] Torrent { get; } = torrent;

        public List<string> Fetched { get; } = [];

        public HttpClient CreateClient(string name) => new(new Handler(this));

        private sealed class Handler(RoutedFactory owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var uri = request.RequestUri!.ToString();
                if (uri.Contains("/search", StringComparison.Ordinal) || uri.Contains("/results?", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                        { Content = new StringContent(owner.SearchJson) });
                }

                owner.Fetched.Add(uri);
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    { Content = new ByteArrayContent(owner.Torrent) });
            }
        }
    }

    private static readonly byte[] TorrentFile = System.Text.Encoding.ASCII.GetBytes(
        "d4:infod6:lengthi12345e4:name8:test.cbz12:piece lengthi16384e6:pieces20:aaaaaaaaaaaaaaaaaaaaee");

    private static string Wire(object? value) => System.Text.Json.JsonSerializer.Serialize(
        value, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

    private (ReleaseController Controller, TorrentUpgradeService Torrents, MakiDbContext Db) ControllerOver(IHttpClientFactory http)
    {
        var service = Build(http);
        var db = _db.NewContext();
        var torrents = new TorrentUpgradeService(db, new UpgradeEvaluationService(db, TestQuality.Create()), service,
            null!, new FakeAppSettings(), TimeProvider.System, new FakeRelatedTitles(), NullLogger<TorrentUpgradeService>.Instance);
        var controller = new ReleaseController(
            service, new ReleaseSearchCache(new MemoryCache(new MemoryCacheOptions())), new TestLocalizer(),
            NullLogger<ReleaseController>.Instance);
        return (controller, torrents, db);
    }

    [Fact]
    public async Task Search_response_hides_the_indexer_key_and_a_grab_still_fetches_the_stored_link()
    {
        var seriesId = _db.SeedSeries("Berserk");
        await ConfigureProwlarr();
        await _settings.SetAsync(SettingKeys.DownloadClientUrl, "http://qbt.test");
        const string download = "http://prowlarr.test/1/download?apikey=SECRETKEY&link=abc&file=x";
        var http = new RoutedFactory(
            "[{\"guid\":\"http://nyaa.test/1?apikey=SECRETKEY&n=1\",\"title\":\"Berserk v01\",\"size\":1000,\"indexer\":\"Nyaa\","
            + "\"seeders\":5,\"leechers\":0,\"protocol\":\"torrent\",\"magnetUrl\":null,"
            + $"\"downloadUrl\":\"{download}\",\"infoUrl\":\"http://nyaa.test/view/1?apikey=SECRETKEY\"}}]",
            TorrentFile);
        var (controller, torrents, db) = ControllerOver(http);
        using var _ = db;

        var search = await controller.Search(seriesId, "berserk", torrents, CancellationToken.None);

        var body = Wire(Assert.IsType<OkObjectResult>(search).Value);
        Assert.DoesNotContain("SECRETKEY", body);
        Assert.DoesNotContain("apikey", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("downloadUrl", body, StringComparison.OrdinalIgnoreCase);
        var row = Assert.Single(Assert.IsType<ReleaseSearchResponse>(((OkObjectResult)search).Value).Releases);
        Assert.Equal("http://nyaa.test/1?n=1", row.Guid);
        Assert.Equal("http://nyaa.test/view/1", row.InfoUrl);

        var grab = await controller.Grab(
            new ReleaseController.GrabRequest(seriesId, new ReleaseController.GrabRelease(row.Guid)),
            new TestCurrentUser(1), db, CancellationToken.None);

        Assert.IsType<OkObjectResult>(grab);
        Assert.Equal([download], http.Fetched);
        Assert.Single(_client.AddedFiles);
    }

    [Fact]
    public async Task Jackett_search_response_keeps_the_proxy_key_out_of_the_guid_and_the_link()
    {
        var seriesId = _db.SeedSeries("Berserk");
        await _settings.SetAsync(SettingKeys.IndexerBackend, "jackett");
        await _settings.SetAsync(SettingKeys.JackettUrl, "http://jackett.test:9117");
        await _settings.SetAsync(SettingKeys.JackettApiKey, "JKEY");
        await _settings.SetAsync(SettingKeys.DownloadClientUrl, "http://qbt.test");
        const string link = "http://jackett.test:9117/dl/nyaasi/?jackett_apikey=JKEY&path=x";
        var http = new RoutedFactory(
            "{\"Results\":[{\"Title\":\"Berserk v01\",\"Link\":\"" + link
            + "\",\"Details\":\"http://nyaa.test/view/1?jackett_apikey=JKEY\","
            + "\"Size\":1000,\"Seeders\":7,\"Peers\":10,\"Tracker\":\"Nyaa.si\"}],\"Indexers\":[]}",
            TorrentFile);
        var (controller, torrents, db) = ControllerOver(http);
        using var _ = db;

        var search = await controller.Search(seriesId, "berserk", torrents, CancellationToken.None);

        var body = Wire(Assert.IsType<OkObjectResult>(search).Value);
        Assert.DoesNotContain("JKEY", body);
        Assert.DoesNotContain("jackett_apikey", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("downloadUrl", body, StringComparison.OrdinalIgnoreCase);

        var row = Assert.Single(Assert.IsType<ReleaseSearchResponse>(((OkObjectResult)search).Value).Releases);
        var grab = await controller.Grab(
            new ReleaseController.GrabRequest(seriesId, new ReleaseController.GrabRelease(row.Guid)),
            new TestCurrentUser(1), db, CancellationToken.None);

        Assert.IsType<OkObjectResult>(grab);
        Assert.Equal([link], http.Fetched);
    }

    [Fact]
    public async Task A_grab_settles_the_pending_proposal_by_the_stored_guid_not_the_redacted_one()
    {
        var seriesId = _db.SeedSeries("Berserk");
        await ConfigureProwlarr();
        await _settings.SetAsync(SettingKeys.DownloadClientUrl, "http://qbt.test");
        const string guid = "http://nyaa.test/1?apikey=SECRETKEY&n=1";
        var http = new RoutedFactory(
            "[{\"guid\":\"" + guid + "\",\"title\":\"Berserk v01\",\"size\":1000,\"indexer\":\"Nyaa\","
            + "\"seeders\":5,\"leechers\":0,\"protocol\":\"torrent\",\"downloadUrl\":\"http://prowlarr.test/1/download\"}]",
            TorrentFile);
        var (controller, torrents, db) = ControllerOver(http);
        using var _ = db;
        db.TorrentProposals.Add(new TorrentProposal { SeriesId = seriesId, ReleaseGuid = guid, Status = TorrentProposalStatus.Pending });
        db.SaveChanges();

        var search = await controller.Search(seriesId, "berserk", torrents, CancellationToken.None);
        var row = Assert.Single(Assert.IsType<ReleaseSearchResponse>(((OkObjectResult)search).Value).Releases);
        await controller.Grab(new ReleaseController.GrabRequest(seriesId, new ReleaseController.GrabRelease(row.Guid)),
            new TestCurrentUser(1), db, CancellationToken.None);

        Assert.Equal(TorrentProposalStatus.Accepted, db.TorrentProposals.Single().Status);
    }

    [Theory]
    [InlineData("http://h/dl?apikey=K&x=1", "http://h/dl?x=1")]
    [InlineData("http://h/dl?x=1&jackett_apikey=K", "http://h/dl?x=1")]
    [InlineData("http://h/dl?API_KEY=K#top", "http://h/dl#top")]
    [InlineData("http://h/dl?apikeys=K", "http://h/dl?apikeys=K")]
    [InlineData("magnet:?xt=urn:btih:abc&apikey=K", "magnet:?xt=urn:btih:abc&apikey=K")]
    [InlineData("http://h/dl", "http://h/dl")]
    public void Api_key_parameters_are_removed_from_a_link_and_nothing_else(string link, string expected) =>
        Assert.Equal(expected, IndexerLinks.WithoutApiKey(link));

    [Fact]
    public async Task Grab_falls_back_to_the_indexers_hash_when_the_url_add_reports_none()
    {
        var seriesId = _db.SeedSeries("Berserk");
        await _settings.SetAsync(SettingKeys.DownloadClientUrl, "http://qbt.test");
        var release = new ReleaseDto("g", "Berserk v01", 1000, "Nyaa", 10, 1, "torrent", "http://jackett.test/dl/nyaasi", null, null,
            InfoHash: "0123456789ABCDEF0123456789ABCDEF01234567");

        var item = await Build(new DownloadLinkFactory("<html>busy</html>"u8.ToArray()))
            .GrabAsync(seriesId, release, DownloadOrigin.Manual, null, null);

        Assert.Equal([release.DownloadUrl!], _client.AddedLinks);
        Assert.Equal("0123456789abcdef0123456789abcdef01234567",
            System.Text.Json.JsonSerializer.Deserialize<ReleaseInfo>(item.ReleaseInfoJson!)!.TorrentHash);
    }

    [Fact]
    public async Task Grab_endpoint_refuses_a_release_the_previous_backend_returned()
    {
        var seriesId = _db.SeedSeries("Berserk");
        await ConfigureProwlarr();
        await _settings.SetAsync(SettingKeys.DownloadClientUrl, "http://qbt.test");
        var service = Build("[" + Release("g", "torrent", 5, magnet: "magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567") + "]");
        var controller = new ReleaseController(
            service, new ReleaseSearchCache(new MemoryCache(new MemoryCacheOptions())), new TestLocalizer(),
            NullLogger<ReleaseController>.Instance);

        using var db = _db.NewContext();
        var torrents = new TorrentUpgradeService(db, new UpgradeEvaluationService(db, TestQuality.Create()), service,
            null!, new FakeAppSettings(), TimeProvider.System, new FakeRelatedTitles(), NullLogger<TorrentUpgradeService>.Instance);
        Assert.IsType<OkObjectResult>(await controller.Search(seriesId, "berserk", torrents, CancellationToken.None));
        await _settings.SetAsync(SettingKeys.IndexerBackend, "jackett");
        var result = await controller.Grab(
            new ReleaseController.GrabRequest(seriesId, new ReleaseController.GrabRelease("g")),
            new TestCurrentUser(1), db, CancellationToken.None);

        Assert.Equal(400, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Empty(_client.AddedLinks);
    }

    [Fact]
    public async Task Grab_endpoint_refuses_a_release_no_search_returned()
    {
        var seriesId = _db.SeedSeries("Berserk");
        await _settings.SetAsync(SettingKeys.DownloadClientUrl, "http://qbt.test");
        var forged = new ReleaseController.GrabRelease("g");

        using var db = _db.NewContext();
        var result = await new ReleaseController(
                Build(), new ReleaseSearchCache(new MemoryCache(new MemoryCacheOptions())), new TestLocalizer(),
                NullLogger<ReleaseController>.Instance)
            .Grab(new ReleaseController.GrabRequest(seriesId, forged), new TestCurrentUser(1), db, CancellationToken.None);

        Assert.Equal(400, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Empty(_client.AddedLinks);
        Assert.Empty(db.DownloadQueue);
    }

    [Fact]
    public async Task Grab_records_the_origin_the_user_and_the_upgrade_info()
    {
        var seriesId = _db.SeedSeries("Berserk");
        await _settings.SetAsync(SettingKeys.DownloadClientUrl, "http://qbt.test");
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
        await _settings.SetAsync(SettingKeys.DownloadClientUrl, "http://qbt.test");
        var release = new ReleaseDto("g", "t", 1, "Nyaa", 1, 0, "torrent", null, null, null);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Build().GrabAsync(seriesId, release, DownloadOrigin.Manual, null, null));
    }

    [Fact]
    public async Task Grab_for_a_missing_series_throws()
    {
        await _settings.SetAsync(SettingKeys.DownloadClientUrl, "http://qbt.test");
        var release = new ReleaseDto("g", "t", 1, "Nyaa", 1, 0, "torrent", "http://x/t.torrent", null, null);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Build().GrabAsync(404, release, DownloadOrigin.Manual, null, null));
    }
}
