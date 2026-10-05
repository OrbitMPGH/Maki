using System.Net;
using System.Text.Json;
using Maki.Core.Configuration;
using Maki.Core.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Core.Tests;

public class ChallengeAwareFetcherTests
{
    private const string Target = "https://example.test/wp-admin/admin-ajax.php";

    private static readonly Dictionary<string, string> Mature = new() { ["toonily-mature"] = "1" };

    /// <summary>Answers the target host by rule and records FlareSolverr's /v1 payloads.</summary>
    private sealed class Handler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> OnTarget { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>direct</html>") };

        public List<HttpRequestMessage> TargetRequests { get; } = [];
        public List<string> TargetBodies { get; } = [];
        public List<JsonDocument> FlarePayloads { get; } = [];

        public int FlareStatus { get; set; } = 200;

        public string FlareCookies { get; set; } =
            """[{"name":"cf_clearance","value":"abc"},{"name":"toonily-mature","value":"1"}]""";

        public TimeSpan FlareDelay { get; set; } = TimeSpan.Zero;

        public int MaxFlareInFlight;
        private int _flareInFlight;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsoluteUri.EndsWith("/v1", StringComparison.Ordinal))
            {
                var inFlight = Interlocked.Increment(ref _flareInFlight);
                try
                {
                    var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                    lock (FlarePayloads)
                    {
                        MaxFlareInFlight = Math.Max(MaxFlareInFlight, inFlight);
                        FlarePayloads.Add(payload);
                    }

                    if (FlareDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(FlareDelay, ct);
                    }

                    var solution = $$"""{"status":"ok","solution":{"status":{{FlareStatus}},"response":"<html>solved</html>","userAgent":"FlareUA","cookies":"""
                        + FlareCookies + "}}";
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(solution, System.Text.Encoding.UTF8, "application/json")
                    };
                }
                finally
                {
                    Interlocked.Decrement(ref _flareInFlight);
                }
            }

            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (TargetRequests)
            {
                TargetRequests.Add(request);
                TargetBodies.Add(body);
            }

            return OnTarget(request);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Settings(string? flareUrl) : IAppSettings
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(key == SettingKeys.FlareSolverrUrl ? flareUrl : null);

        public Task SetAsync(string key, string? value, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static (ChallengeAwareFetcher Fetcher, Handler Handler) Build(string? flareUrl = "http://flare.test:8191")
    {
        var handler = new Handler();
        var factory = new Factory(handler);
        var fetcher = new ChallengeAwareFetcher(
            factory, new FlareSolverrClient(factory), new Settings(flareUrl), NullLogger<ChallengeAwareFetcher>.Instance);
        return (fetcher, handler);
    }

    [Fact]
    public async Task Direct_post_sends_form_body_and_request_cookies()
    {
        var (fetcher, handler) = Build();

        var html = await fetcher.FetchAsync(new HtmlFetchRequest(Target, Mature, "action=search&q=x"));

        Assert.Equal("<html>direct</html>", html);
        var sent = Assert.Single(handler.TargetRequests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("application/x-www-form-urlencoded", sent.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("action=search&q=x", handler.TargetBodies[0]);
        Assert.Equal("toonily-mature=1", sent.Headers.GetValues("Cookie").Single());
        Assert.Empty(handler.FlarePayloads);
    }

    [Fact]
    public async Task Plain_get_stays_a_get_without_cookie_header()
    {
        var (fetcher, handler) = Build();

        await fetcher.GetHtmlAsync(Target);

        var sent = Assert.Single(handler.TargetRequests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Null(sent.Content);
        Assert.False(sent.Headers.Contains("Cookie"));
    }

    [Fact]
    public async Task Challenge_hands_form_body_and_cookies_to_flaresolverr_as_request_post()
    {
        var (fetcher, handler) = Build();
        handler.OnTarget = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);

        var html = await fetcher.FetchAsync(new HtmlFetchRequest(Target, Mature, "action=search&q=x"));

        Assert.Equal("<html>solved</html>", html);
        var payload = Assert.Single(handler.FlarePayloads).RootElement;
        Assert.Equal("request.post", payload.GetProperty("cmd").GetString());
        Assert.Equal(Target, payload.GetProperty("url").GetString());
        Assert.Equal("action=search&q=x", payload.GetProperty("postData").GetString());
        var cookie = Assert.Single(payload.GetProperty("cookies").EnumerateArray());
        Assert.Equal("toonily-mature", cookie.GetProperty("name").GetString());
        Assert.Equal("1", cookie.GetProperty("value").GetString());
    }

    [Fact]
    public async Task Challenge_on_a_plain_get_sends_request_get_without_post_or_cookie_keys()
    {
        var (fetcher, handler) = Build();
        handler.OnTarget = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        await fetcher.GetHtmlAsync(Target);

        var payload = Assert.Single(handler.FlarePayloads).RootElement;
        Assert.Equal("request.get", payload.GetProperty("cmd").GetString());
        Assert.False(payload.TryGetProperty("postData", out _));
        Assert.False(payload.TryGetProperty("cookies", out _));
    }

    [Fact]
    public async Task Solved_session_cookies_are_merged_under_the_request_cookies_on_the_next_direct_call()
    {
        var (fetcher, handler) = Build();
        handler.OnTarget = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);
        await fetcher.GetHtmlAsync(Target);

        handler.OnTarget = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>ok</html>") };
        await fetcher.FetchAsync(new HtmlFetchRequest(Target, new Dictionary<string, string> { ["toonily-mature"] = "2" }));

        var second = handler.TargetRequests[^1];
        var cookies = second.Headers.GetValues("Cookie").Single().Split("; ");
        Assert.Contains("cf_clearance=abc", cookies);
        Assert.Contains("toonily-mature=2", cookies);
        Assert.DoesNotContain("toonily-mature=1", cookies);
        Assert.Equal("FlareUA", second.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task Unchallenged_fetch_works_without_a_flaresolverr_url()
    {
        var (fetcher, handler) = Build(flareUrl: null);

        Assert.Equal("<html>direct</html>", await fetcher.GetHtmlAsync(Target));
        Assert.Empty(handler.FlarePayloads);
    }

    [Fact]
    public async Task Challenge_without_a_flaresolverr_url_throws_instead_of_returning_the_shell()
    {
        var (fetcher, handler) = Build(flareUrl: null);
        handler.OnTarget = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fetcher.FetchAsync(new HtmlFetchRequest(Target, Mature, "a=b")));
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_non_challenge_error_status_is_rethrown_without_flaresolverr(HttpStatusCode status)
    {
        var (fetcher, handler) = Build();
        handler.OnTarget = _ => new HttpResponseMessage(status);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => fetcher.GetHtmlAsync(Target));

        Assert.Equal(status, ex.StatusCode);
        Assert.Empty(handler.FlarePayloads);
    }

    [Fact]
    public async Task A_transport_error_is_rethrown_without_flaresolverr()
    {
        var (fetcher, handler) = Build();
        handler.OnTarget = _ => throw new HttpRequestException("connection refused");

        await Assert.ThrowsAsync<HttpRequestException>(() => fetcher.GetHtmlAsync(Target));
        Assert.Empty(handler.FlarePayloads);
    }

    [Fact]
    public async Task A_challenge_body_on_a_200_goes_to_flaresolverr()
    {
        var (fetcher, handler) = Build();
        handler.OnTarget = _ => Ok("<html><head><title>Just a moment...</title></head></html>");

        Assert.Equal("<html>solved</html>", await fetcher.GetHtmlAsync(Target));
        Assert.Single(handler.FlarePayloads);
    }

    [Fact]
    public async Task A_cf_mitigated_challenge_header_goes_to_flaresolverr()
    {
        var (fetcher, handler) = Build();
        handler.OnTarget = _ =>
        {
            var response = Ok("<html>looks fine</html>");
            response.Headers.Add("cf-mitigated", "challenge");
            return response;
        };

        Assert.Equal("<html>solved</html>", await fetcher.GetHtmlAsync(Target));
        Assert.Single(handler.FlarePayloads);
    }

    [Fact]
    public async Task A_healthy_page_carrying_the_js_detections_script_is_not_a_challenge()
    {
        var (fetcher, handler) = Build();
        const string page = """<html><body>results</body><script src="/cdn-cgi/challenge-platform/scripts/jsd/main.js"></script></html>""";
        handler.OnTarget = _ => Ok(page);

        Assert.Equal(page, await fetcher.GetHtmlAsync(Target));
        Assert.Empty(handler.FlarePayloads);
    }

    [Fact]
    public async Task A_flaresolverr_error_status_throws_instead_of_returning_the_page()
    {
        var (fetcher, handler) = Build();
        handler.OnTarget = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);
        handler.FlareStatus = 404;

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => fetcher.GetHtmlAsync(Target));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task A_404_keeps_the_cached_clearance_for_the_next_call()
    {
        var (fetcher, handler) = Build();
        handler.OnTarget = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);
        await fetcher.GetHtmlAsync(Target);

        handler.OnTarget = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        await Assert.ThrowsAsync<HttpRequestException>(() => fetcher.GetHtmlAsync(Target));

        handler.OnTarget = _ => Ok("<html>ok</html>");
        Assert.Equal("<html>ok</html>", await fetcher.GetHtmlAsync(Target));
        Assert.Contains("cf_clearance=abc", handler.TargetRequests[^1].Headers.GetValues("Cookie").Single());
        Assert.Single(handler.FlarePayloads);
    }

    [Fact]
    public async Task Duplicate_cookie_names_prefer_the_one_scoped_to_the_target_host()
    {
        var (fetcher, handler) = Build();
        handler.FlareCookies = """
            [{"name":"cf_clearance","value":"right","domain":".example.test"},
             {"name":"cf_clearance","value":"wrong","domain":".elsewhere.test"},
             {"name":"consent","value":"old","domain":"example.test"},
             {"name":"consent","value":"new","domain":".example.test"}]
            """;

        var session = await fetcher.GetBrowserSessionAsync(Target);

        Assert.Equal("right", session.Cookies["cf_clearance"]);
        Assert.Equal("new", session.Cookies["consent"]);
    }

    [Fact]
    public async Task Concurrent_fetches_on_a_cold_host_share_one_solve()
    {
        var (fetcher, handler) = Build();
        handler.FlareDelay = TimeSpan.FromMilliseconds(200);
        handler.OnTarget = request =>
            request.Headers.TryGetValues("Cookie", out var cookies) && cookies.Single().Contains("cf_clearance")
                ? Ok("<html>ok</html>")
                : new HttpResponseMessage(HttpStatusCode.Forbidden);

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => fetcher.GetHtmlAsync(Target)));

        Assert.Single(handler.FlarePayloads);
        Assert.Equal(5, results.Count(r => r == "<html>ok</html>"));
    }

    [Fact]
    public async Task Flaresolverr_runs_at_most_two_solves_at_once()
    {
        var (fetcher, handler) = Build();
        handler.FlareDelay = TimeSpan.FromMilliseconds(150);
        handler.OnTarget = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);

        await Task.WhenAll(Enumerable.Range(0, 5).Select(i => fetcher.GetHtmlAsync($"https://host{i}.test/")));

        Assert.Equal(5, handler.FlarePayloads.Count);
        Assert.Equal(2, handler.MaxFlareInFlight);
    }
}
