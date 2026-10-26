using System.Net;
using Maki.Core.Indexers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Core.Tests;

public class JackettClientTests
{
    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(answer(request));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public List<string> Names { get; } = [];

        public HttpClient CreateClient(string name)
        {
            Names.Add(name);
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    private sealed class CapturingLogger : ILogger<JackettClient>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add($"{logLevel}: {formatter(state, exception)}");
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static IndexerSettings Settings(IReadOnlyList<string>? ids = null, IReadOnlyList<int>? categories = null) =>
        new(IndexerBackend.Jackett, "http://jackett.test:9117/", "secret key", ids, categories);

    private static (JackettClient Client, RecordingHandler Handler, Factory Factory) Build(
        Func<HttpRequestMessage, HttpResponseMessage> answer, ILogger<JackettClient>? logger = null)
    {
        var handler = new RecordingHandler(answer);
        var factory = new Factory(handler);
        return (new JackettClient(factory, logger ?? NullLogger<JackettClient>.Instance), handler, factory);
    }

    private const string SearchBody = """
        {
          "Results": [
            {"Title":"Berserk v01 (Digital)","Guid":"https://nyaa.si/view/1","Link":"http://jackett.test:9117/dl/nyaasi/?jackett_apikey=secret&path=abc",
             "Details":"https://nyaa.si/view/1","Size":123456,"Seeders":12,"Peers":15,
             "InfoHash":"0123456789abcdef0123456789abcdef01234567","MagnetUri":null,"Tracker":"Nyaa.si","TrackerId":"nyaasi"},
            {"Title":"Berserk v02","Guid":null,"Link":null,"MagnetUri":"magnet:?xt=urn:btih:abc","Size":10,"Seeders":null,"Peers":4,
             "Tracker":null,"TrackerId":"anidex"},
            {"Title":null,"Guid":"https://x/2","Link":"https://x/2"}
          ],
          "Indexers": [
            {"ID":"nyaasi","Name":"Nyaa.si","Status":2,"Results":2,"Error":null},
            {"ID":"broken","Name":"Broken Tracker","Status":1,"Results":0,"Error":"Cloudflare challenge"}
          ]
        }
        """;

    [Fact]
    public async Task Search_maps_results_and_reads_peers_as_seeders_plus_leechers()
    {
        var (client, _, factory) = Build(_ => Ok(SearchBody));

        var releases = await client.SearchAsync(Settings(), "berserk");

        Assert.Equal(["jackett"], factory.Names);
        Assert.Equal(2, releases.Count);
        var first = releases[0];
        Assert.Equal("https://nyaa.si/view/1", first.Guid);
        Assert.Equal("Nyaa.si", first.Indexer);
        Assert.Equal(12, first.Seeders);
        Assert.Equal(3, first.Leechers);
        Assert.Equal(123456, first.Size);
        Assert.Equal("http://jackett.test:9117/dl/nyaasi/?jackett_apikey=secret&path=abc", first.DownloadUrl);
        Assert.Equal("https://nyaa.si/view/1", first.InfoUrl);
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", first.InfoHash);
        Assert.Equal("torrent", first.Protocol);

        var second = releases[1];
        Assert.Equal("magnet:?xt=urn:btih:abc", second.Guid);
        Assert.Equal("anidex", second.Indexer);
        Assert.Null(second.Leechers);
        Assert.Equal("magnet:?xt=urn:btih:abc", second.MagnetUrl);
    }

    [Fact]
    public async Task The_details_page_is_the_guid_before_the_key_carrying_proxy_link()
    {
        var (client, _, _) = Build(_ => Ok("""
            {"Results":[{"Title":"Berserk v03","Guid":null,"Details":"https://nyaa.si/view/3",
              "Link":"http://jackett.test:9117/dl/nyaasi/?jackett_apikey=secret&path=abc"}],"Indexers":[]}
            """));

        var release = Assert.Single(await client.SearchAsync(Settings(), "berserk"));

        Assert.Equal("https://nyaa.si/view/3", release.Guid);
    }

    [Fact]
    public async Task A_guid_that_falls_back_to_the_proxy_link_drops_the_key_and_the_download_url_keeps_it()
    {
        var (client, _, _) = Build(_ => Ok("""
            {"Results":[{"Title":"Berserk v05","Guid":null,"Details":null,
              "Link":"http://jackett.test:9117/dl/nyaasi/?jackett_apikey=secret&path=abc"}],"Indexers":[]}
            """));

        var release = Assert.Single(await client.SearchAsync(Settings(), "berserk"));

        Assert.Equal("http://jackett.test:9117/dl/nyaasi/?path=abc", release.Guid);
        Assert.Equal("http://jackett.test:9117/dl/nyaasi/?jackett_apikey=secret&path=abc", release.DownloadUrl);
    }

    [Fact]
    public async Task A_magnet_link_is_a_magnet_url_and_not_a_download_url()
    {
        const string magnet = "magnet:?xt=urn:btih:abc";
        var (client, _, _) = Build(_ => Ok($$"""
            {"Results":[{"Title":"Berserk v04","Guid":"g4","Link":"{{magnet}}","MagnetUri":null}],"Indexers":[]}
            """));

        var release = Assert.Single(await client.SearchAsync(Settings(), "berserk"));

        Assert.Equal(magnet, release.MagnetUrl);
        Assert.Null(release.DownloadUrl);
    }

    [Fact]
    public async Task A_non_json_answer_is_a_failed_search()
    {
        var (client, _, _) = Build(_ => Ok("<html><body>Sign in"));

        await Assert.ThrowsAsync<IndexerSearchException>(() => client.SearchAsync(Settings(), "berserk"));
    }

    [Fact]
    public async Task A_failing_indexer_is_logged_by_name_without_the_key_and_the_rest_are_kept()
    {
        var logger = new CapturingLogger();
        var (client, _, _) = Build(_ => Ok(SearchBody), logger);

        var releases = await client.SearchAsync(Settings(), "berserk");

        Assert.Equal(2, releases.Count);
        var line = Assert.Single(logger.Lines);
        Assert.StartsWith("Warning:", line);
        Assert.Contains("Broken Tracker", line);
        Assert.Contains("Cloudflare challenge", line);
        Assert.DoesNotContain("secret", line);
    }

    [Fact]
    public async Task Every_indexer_failing_with_no_results_is_a_failed_search()
    {
        var (client, _, _) = Build(_ => Ok("""
            {"Results":[],"Indexers":[{"ID":"a","Name":"A","Status":1,"Results":0,"Error":"timeout"},
                                       {"ID":"b","Name":"B","Status":1,"Results":0,"Error":"401"}]}
            """));

        await Assert.ThrowsAsync<IndexerSearchException>(() => client.SearchAsync(Settings(), "berserk"));
    }

    [Fact]
    public async Task No_results_from_healthy_indexers_is_an_empty_search()
    {
        var (client, _, _) = Build(_ => Ok("""{"Results":[],"Indexers":[{"ID":"a","Name":"A","Status":2,"Results":0,"Error":null}]}"""));

        Assert.Empty(await client.SearchAsync(Settings(), "berserk"));
    }

    [Fact]
    public async Task Search_escapes_the_query_and_sends_each_tracker_and_category()
    {
        var (client, handler, _) = Build(_ => Ok("""{"Results":[],"Indexers":[]}"""));

        await client.SearchAsync(Settings(["nyaasi", "anime bytes"], [7030, 8000]), "Kaguya & Co");

        var uri = Assert.Single(handler.Requests);
        Assert.Equal("/api/v2.0/indexers/all/results", uri.AbsolutePath);
        Assert.Equal(
            "?apikey=secret%20key&Query=Kaguya%20%26%20Co&Category%5B%5D=7030&Category%5B%5D=8000" +
            "&Tracker%5B%5D=nyaasi&Tracker%5B%5D=anime%20bytes",
            uri.Query);
    }

    [Fact]
    public async Task A_download_link_redirecting_to_a_magnet_returns_the_magnet()
    {
        const string magnet = "magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567&dn=x";
        var (client, _, factory) = Build(_ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri(magnet) },
        });

        var link = await client.FetchTorrentAsync("http://jackett.test:9117/dl/nyaasi/?jackett_apikey=secret&path=abc");

        Assert.Equal(magnet, link.Magnet);
        Assert.Null(link.File);
        Assert.Equal([ReleaseIndexerBase.DownloadHttpClientName], factory.Names);
    }

    [Fact]
    public async Task The_indexer_list_reads_ids_titles_and_flattened_categories()
    {
        var (client, handler, _) = Build(_ => Ok("""
            <?xml version="1.0" encoding="UTF-8"?>
            <indexers>
              <indexer id="nyaasi" configured="true">
                <title>Nyaa.si</title>
                <description>A public torrent site</description>
                <link>https://nyaa.si/</link>
                <language>en-US</language>
                <type>public</type>
                <caps>
                  <categories>
                    <category id="5000" name="TV">
                      <subcat id="5070" name="TV/Anime" />
                    </category>
                    <category id="7000" name="Books">
                      <subcat id="7030" name="Books/Comics" />
                    </category>
                    <category id="5070" name="TV/Anime" />
                    <category id="100001" name="" />
                  </categories>
                </caps>
              </indexer>
              <indexer id="animebytes" configured="true">
                <title>AnimeBytes</title>
                <caps><categories /></caps>
              </indexer>
            </indexers>
            """));

        var indexers = await client.GetIndexersAsync(Settings());

        var uri = Assert.Single(handler.Requests);
        Assert.Equal("/api/v2.0/indexers/all/results/torznab/api", uri.AbsolutePath);
        Assert.Equal("?apikey=secret%20key&t=indexers&configured=true", uri.Query);
        Assert.Equal(["nyaasi", "animebytes"], indexers.Select(i => i.Id));
        var nyaa = indexers[0];
        Assert.Equal("Nyaa.si", nyaa.Name);
        Assert.True(nyaa.Enabled);
        Assert.Equal("torrent", nyaa.Protocol);
        Assert.Equal([5000, 5070, 7000, 7030], nyaa.Categories.Select(c => c.Id));
        Assert.Equal("Books/Comics", nyaa.Categories.Single(c => c.Id == 7030).Name);
        Assert.Empty(indexers[1].Categories);
    }

    [Fact]
    public async Task An_error_document_for_the_indexer_list_throws()
    {
        var (client, _, _) = Build(_ => Ok("""<error code="100" description="Invalid API Key" />"""));

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => client.GetIndexersAsync(Settings()));
    }

    [Fact]
    public async Task Ping_fails_on_an_error_document_even_with_a_200()
    {
        var (client, handler, _) = Build(_ => Ok("""<?xml version="1.0" encoding="UTF-8"?><error code="100" description="Invalid API Key" />"""));

        Assert.False(await client.PingAsync(Settings()));
        Assert.Equal("?apikey=secret%20key&t=caps", Assert.Single(handler.Requests).Query);
    }

    [Fact]
    public async Task Ping_passes_on_a_caps_document_and_fails_on_a_non_success_status()
    {
        var (good, _, _) = Build(_ => Ok("""<caps><server title="Jackett" /><searching /></caps>"""));
        var (bad, _, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var (html, _, _) = Build(_ => Ok("<html><body>login"));

        Assert.True(await good.PingAsync(Settings()));
        Assert.False(await bad.PingAsync(Settings()));
        Assert.False(await html.PingAsync(Settings()));
    }
}
