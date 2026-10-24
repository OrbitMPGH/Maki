using System.Net;
using Maki.Core.Indexers;

namespace Maki.Core.Tests;

public class ProwlarrClientTests
{
    private sealed class RecordingHandler(string body) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static IndexerSettings Settings(IReadOnlyList<string>? ids = null, IReadOnlyList<int>? categories = null) =>
        new(IndexerBackend.Prowlarr, "http://prowlarr.test:9696", "pkey", ids, categories);

    [Fact]
    public async Task Stored_ids_are_sent_as_ints_and_non_numeric_ones_are_skipped()
    {
        var handler = new RecordingHandler("[]");

        await new ProwlarrClient(new Factory(handler)).SearchAsync(Settings(["3", "nyaasi", " 12"], [7030]), "berserk");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("?query=berserk&type=search&limit=100&indexerIds=3&indexerIds=12&categories=7030", request.RequestUri!.Query);
        Assert.Equal(["pkey"], request.Headers.GetValues("X-Api-Key"));
    }

    [Fact]
    public async Task Search_maps_releases_including_the_infohash()
    {
        var handler = new RecordingHandler("""
            [{"guid":"g1","title":"Berserk v01","size":10,"indexerId":3,"indexer":"Nyaa","seeders":5,"leechers":2,
              "downloadUrl":"http://prowlarr.test:9696/3/download?link=x","magnetUrl":null,"infoHash":"ABCDEF0123456789ABCDEF0123456789ABCDEF01",
              "protocol":"torrent","infoUrl":"https://nyaa.si/view/1","ageMinutes":3.5}]
            """);

        var release = Assert.Single(await new ProwlarrClient(new Factory(handler)).SearchAsync(Settings(), "berserk"));

        Assert.Equal(new IndexerRelease("g1", "Berserk v01", 10, "Nyaa", 5, 2,
            "http://prowlarr.test:9696/3/download?link=x", null, "ABCDEF0123456789ABCDEF0123456789ABCDEF01", "torrent",
            "https://nyaa.si/view/1"), release);
    }

    private const string DownloadLink = "http://prowlarr.test:9696/3/download?link=x";
    private const string ProxyMagnet = "http://prowlarr.test:9696/3/download?apikey=pkey&link=m";

    [Theory]
    [InlineData(DownloadLink, ProxyMagnet, DownloadLink, null)]
    [InlineData(null, ProxyMagnet, ProxyMagnet, null)]
    [InlineData(DownloadLink, "magnet:?xt=urn:btih:abc", DownloadLink, "magnet:?xt=urn:btih:abc")]
    public async Task Only_a_real_magnet_stays_a_magnet_and_a_proxy_link_is_a_download_url(
        string? downloadUrl, string magnetUrl, string expectedDownload, string? expectedMagnet)
    {
        var download = downloadUrl is null ? "null" : $"\"{downloadUrl}\"";
        var handler = new RecordingHandler($$"""
            [{"guid":"g1","title":"Berserk v01","size":10,"indexer":"Nyaa","downloadUrl":{{download}},
              "magnetUrl":"{{magnetUrl}}","protocol":"torrent"}]
            """);

        var release = Assert.Single(await new ProwlarrClient(new Factory(handler)).SearchAsync(Settings(), "berserk"));

        Assert.Equal(expectedDownload, release.DownloadUrl);
        Assert.Equal(expectedMagnet, release.MagnetUrl);
    }

    [Fact]
    public async Task A_non_json_answer_is_a_failed_search()
    {
        var client = new ProwlarrClient(new Factory(new RecordingHandler("<html><body>Sign in")));

        await Assert.ThrowsAsync<IndexerSearchException>(() => client.SearchAsync(Settings(), "berserk"));
    }

    [Fact]
    public async Task Indexers_come_back_with_string_ids_and_flattened_named_categories()
    {
        var handler = new RecordingHandler("""
            [{"id":7,"name":"Nyaa","enable":true,"protocol":"torrent","capabilities":{"categories":[
              {"id":5000,"name":"TV","subCategories":[{"id":5070,"name":"TV/Anime","subCategories":[]}]},
              {"id":5070,"name":"TV/Anime","subCategories":null},
              {"id":100001,"name":null,"subCategories":null}]}},
             {"id":9,"name":"Off","enable":false,"protocol":"usenet","capabilities":null}]
            """);

        var indexers = await new ProwlarrClient(new Factory(handler)).GetIndexersAsync(Settings());

        Assert.Equal(["7", "9"], indexers.Select(i => i.Id));
        Assert.Equal([5000, 5070], indexers[0].Categories.Select(c => c.Id));
        Assert.False(indexers[1].Enabled);
        Assert.Empty(indexers[1].Categories);
    }
}
