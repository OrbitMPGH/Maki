using System.Net;
using System.Text;
using System.Text.Json;
using Maki.Metadata.MangaBaka;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Metadata.Tests;

/// <summary>The API fallback, with no local dump present. No network: every response is canned.</summary>
public class MangaBakaProviderApiTests
{
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _routes = [];
        public List<string> Requested { get; } = [];

        public Handler On(string path, string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _routes[path] = (status, body);
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requested.Add(path);
            var (status, body) = _routes.TryGetValue(path, out var route)
                ? route
                : (HttpStatusCode.NotFound, """{"status":404}""");
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.mangabaka.test/") };
    }

    private static MangaBakaProvider Provider(Handler handler)
    {
        var missing = Path.Combine(Path.GetTempPath(), $"maki-no-dump-{Guid.NewGuid():N}.db");
        var store = new MangaBakaLocalStore(
            new MangaBakaDumpOptions(missing, Path.GetTempPath()),
            new FakeAppSettings(),
            NullLogger<MangaBakaLocalStore>.Instance);
        return new MangaBakaProvider(new Factory(handler), store, NullLogger<MangaBakaProvider>.Instance);
    }

    [Fact]
    public void Counts_sent_as_strings_or_fractions_parse()
    {
        var series = JsonSerializer.Deserialize<MangaBakaSeries>(
            """{"id":42,"title":"Frieren","total_chapters":"10","final_volume":"12.5","merged_with":"7"}""")!;

        Assert.Equal(10, series.TotalChapters);
        Assert.Equal(12, series.FinalVolume);
        Assert.Equal(7, series.MergedWith);
    }

    [Fact]
    public void Counts_sent_as_numbers_null_or_junk_parse()
    {
        var series = JsonSerializer.Deserialize<MangaBakaSeries>(
            """{"id":42,"title":"Frieren","total_chapters":112.5,"final_volume":null,"merged_with":"n/a"}""")!;

        Assert.Equal(112, series.TotalChapters);
        Assert.Null(series.FinalVolume);
        Assert.Null(series.MergedWith);
    }

    [Fact]
    public async Task A_string_payload_maps_through_the_api_path()
    {
        var handler = new Handler().On("/v1/series/42",
            """{"status":200,"data":{"id":42,"title":"Frieren","state":"active","total_chapters":"10","final_volume":"2"}}""");

        var metadata = await Provider(handler).GetAsync("42");

        Assert.NotNull(metadata);
        Assert.Equal(10, metadata.TotalChapters);
        Assert.Equal(2, metadata.TotalVolumes);
        Assert.True(metadata.Partial);
    }

    [Fact]
    public async Task A_404_is_not_found_rather_than_an_exception()
    {
        var handler = new Handler().On("/v1/series/42", """{"status":404,"message":"not found"}""", HttpStatusCode.NotFound);

        Assert.Null(await Provider(handler).GetAsync("42"));
    }

    [Fact]
    public async Task A_server_error_still_throws()
    {
        var handler = new Handler().On("/v1/series/42", "{}", HttpStatusCode.InternalServerError);

        await Assert.ThrowsAsync<HttpRequestException>(() => Provider(handler).GetAsync("42"));
    }

    [Fact]
    public async Task A_merge_is_followed_to_its_canonical_series()
    {
        var handler = new Handler()
            .On("/v1/series/1", """{"data":{"id":1,"title":"Old","state":"merged","merged_with":"2"}}""")
            .On("/v1/series/2", """{"data":{"id":2,"title":"New","state":"active"}}""");

        var metadata = await Provider(handler).GetAsync("1");

        Assert.Equal("2", metadata?.ProviderId);
    }

    [Fact]
    public async Task A_merge_loop_stops_at_the_hop_limit()
    {
        var handler = new Handler()
            .On("/v1/series/1", """{"data":{"id":1,"title":"A","state":"merged","merged_with":2}}""")
            .On("/v1/series/2", """{"data":{"id":2,"title":"B","state":"merged","merged_with":1}}""");

        Assert.Null(await Provider(handler).GetAsync("1"));
        Assert.Equal(MangaBakaProvider.MaxMergeHops + 1, handler.Requested.Count);
    }

    [Theory]
    [InlineData("../my/library")]
    [InlineData("42?x=1")]
    [InlineData("-1")]
    [InlineData("")]
    public async Task An_id_that_is_not_a_plain_number_never_reaches_the_api(string id)
    {
        var handler = new Handler();

        Assert.Null(await Provider(handler).GetAsync(id));
        Assert.Empty(handler.Requested);
    }
}
