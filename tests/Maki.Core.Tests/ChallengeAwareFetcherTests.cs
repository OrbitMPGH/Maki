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

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsoluteUri.EndsWith("/v1", StringComparison.Ordinal))
            {
                FlarePayloads.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)));
                const string solution = """
                    {"status":"ok","solution":{"status":200,"response":"<html>solved</html>",
                    "userAgent":"FlareUA","cookies":[{"name":"cf_clearance","value":"abc"},{"name":"toonily-mature","value":"1"}]}}
                    """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(solution, System.Text.Encoding.UTF8, "application/json")
                };
            }

            TargetRequests.Add(request);
            TargetBodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
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
    public async Task Challenge_without_a_flaresolverr_url_throws_instead_of_returning_the_shell()
    {
        var (fetcher, handler) = Build(flareUrl: null);
        handler.OnTarget = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fetcher.FetchAsync(new HtmlFetchRequest(Target, Mature, "a=b")));
    }
}
