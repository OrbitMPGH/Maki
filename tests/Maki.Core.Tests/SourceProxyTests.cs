using System.Net;
using Maki.Core.Configuration;
using Maki.Core.Http;
using Maki.Core.Http.Proxy;

namespace Maki.Core.Tests;

public class SourceProxyTests
{
    [Theory]
    [InlineData("http://192.168.1.20:3128", "http://192.168.1.20:3128/")]
    [InlineData(" http://proxy.lan:8888/ ", "http://proxy.lan:8888/")]
    [InlineData("http://[fd00::5]:3128", "http://[fd00::5]:3128/")]
    public void Accepts_plain_http_proxies_with_a_port(string value, string expected)
    {
        Assert.Equal(new Uri(expected), SourceProxyUrl.Parse(value, out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("", "error.settings.sourceProxyInvalidUrl")]
    [InlineData("proxy.lan:3128", "error.settings.sourceProxyHttpOnly")]
    [InlineData("http://proxy.lan", "error.settings.sourceProxyInvalidUrl")]
    [InlineData("http://[fd00::5]", "error.settings.sourceProxyInvalidUrl")]
    [InlineData("http://proxy.lan:3128/path", "error.settings.sourceProxyInvalidUrl")]
    [InlineData("http://proxy.lan:3128/?a=1", "error.settings.sourceProxyInvalidUrl")]
    [InlineData("https://proxy.lan:3128", "error.settings.sourceProxyHttpOnly")]
    [InlineData("socks5://proxy.lan:1080", "error.settings.sourceProxyHttpOnly")]
    [InlineData("http://user:pass@proxy.lan:3128", "error.settings.sourceProxyNoCredentials")]
    public void Refuses_anything_else(string value, string expectedError)
    {
        Assert.Null(SourceProxyUrl.Parse(value, out var error));
        Assert.Equal(expectedError, error);
    }

    [Fact]
    public void The_document_round_trips_and_reads_names_case_insensitively()
    {
        var config = new SourceProxyConfig(
            [new SourceProxyConfig.Entry(SourceProxyConfig.DefaultId, "http://192.168.1.20:3128")],
            new Dictionary<string, string> { ["mangafire"] = SourceProxyConfig.DefaultId });

        var parsed = SourceProxyConfig.Parse(config.Serialize());

        Assert.Equal("http://192.168.1.20:3128", parsed.DefaultUrl);
        Assert.Equal(SourceProxyConfig.DefaultId, parsed.Sources["MangaFire"]);
        Assert.Null(SourceProxyConfig.Empty.Serialize());
        Assert.Same(SourceProxyConfig.Empty, SourceProxyConfig.Parse("{not json"));
    }

    private sealed class MemorySettings : IAppSettings
    {
        public Dictionary<string, string?> Values { get; } = [];
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult(Values.GetValueOrDefault(key));

        public Task SetAsync(string key, string? value, CancellationToken ct = default)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task The_resolver_follows_saves_and_leaves_other_sources_direct()
    {
        var settings = new MemorySettings();
        var resolver = new SourceProxyResolver(settings);
        Assert.True((await resolver.ResolveAsync("mangafire")).IsDirect);

        settings.Values[SettingKeys.SourceProxy] = """{"proxies":[{"id":"default","url":"http://192.168.1.20:3128"}],"sources":{"mangafire":"default"}}""";
        Assert.Equal(new Uri("http://192.168.1.20:3128"), (await resolver.ResolveAsync("mangafire")).Proxy);
        Assert.True((await resolver.ResolveAsync("mangadex")).IsDirect);

        settings.Values[SettingKeys.SourceProxy] = """{"proxies":[{"id":"default","url":"http://10.0.0.9:8888"}],"sources":{"mangafire":"default"}}""";
        Assert.Equal("http://10.0.0.9:8888", (await resolver.ResolveAsync("mangafire")).Fingerprint);
    }

    [Fact]
    public async Task An_assignment_to_a_missing_proxy_fails_rather_than_going_direct()
    {
        var settings = new MemorySettings();
        settings.Values[SettingKeys.SourceProxy] = """{"proxies":[],"sources":{"mangafire":"default"}}""";

        var ex = await Assert.ThrowsAsync<SourceProxyException>(() => new SourceProxyResolver(settings).ResolveAsync("mangafire"));
        Assert.Equal(SourceProxyFailure.Misconfigured, ex.Failure);
    }

    private sealed class RecordingBuild
    {
        public List<Uri?> Built { get; } = [];

        public HttpMessageHandler Build(Uri? proxy)
        {
            lock (Built)
            {
                Built.Add(proxy);
            }

            return new StubHandler(proxy);
        }
    }

    private sealed class StubHandler(Uri? proxy) : HttpMessageHandler
    {
        public bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(proxy?.ToString() ?? "direct") };
            return Task.FromResult(response);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private static async Task<string> SendAsync(HttpMessageInvoker invoker, ProxyRoute? route)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://8.8.8.8/");
        route?.ApplyTo(request);
        using var response = await invoker.SendAsync(request, CancellationToken.None);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task Requests_go_out_on_the_inner_handler_for_their_route()
    {
        var build = new RecordingBuild();
        using var invoker = new HttpMessageInvoker(new ProxyRoutingHandler(build.Build));
        var lan = ProxyRoute.Via(new Uri("http://192.168.1.20:3128"));

        Assert.Equal("direct", await SendAsync(invoker, null));
        Assert.Equal("http://192.168.1.20:3128/", await SendAsync(invoker, lan));
        Assert.Equal("http://192.168.1.20:3128/", await SendAsync(invoker, ProxyRoute.Via(new Uri("http://192.168.1.20:3128/"))));
        Assert.Equal("direct", await SendAsync(invoker, ProxyRoute.Direct));

        Assert.Equal([null, new Uri("http://192.168.1.20:3128")], build.Built);
    }

    [Fact]
    public async Task Concurrent_first_use_builds_one_inner_handler_per_route()
    {
        var build = new RecordingBuild();
        using var invoker = new HttpMessageInvoker(new ProxyRoutingHandler(build.Build));
        var lan = ProxyRoute.Via(new Uri("http://192.168.1.20:3128"));

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => SendAsync(invoker, lan)));

        Assert.Single(build.Built);
    }

    [Fact]
    public async Task A_closed_proxy_port_is_reported_as_unreachable()
    {
        using var invoker = new HttpMessageInvoker(new ProxyRoutingHandler(PublicAddressGuard.CreateManualRedirectHandler));
        // A port that was just free: nothing listens there.
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var ex = await Assert.ThrowsAsync<SourceProxyException>(
            () => SendAsync(invoker, ProxyRoute.Via(new Uri($"http://127.0.0.1:{port}"))));
        Assert.Equal(SourceProxyFailure.Unreachable, ex.Failure);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(502)]
    [InlineData(407)]
    public async Task A_proxy_that_refuses_the_tunnel_is_reported_as_rejected(int status)
    {
        await using var proxy = new FakeConnectProxy(status);
        using var invoker = new HttpMessageInvoker(new ProxyRoutingHandler(PublicAddressGuard.CreateManualRedirectHandler));

        var ex = await Assert.ThrowsAsync<SourceProxyException>(() => SendAsync(invoker, ProxyRoute.Via(proxy.Url)));
        Assert.Equal(SourceProxyFailure.Rejected, ex.Failure);
    }

    [Fact]
    public async Task A_407_on_plain_http_is_reported_as_rejected()
    {
        await using var proxy = new FakeConnectProxy(407);
        using var invoker = new HttpMessageInvoker(new ProxyRoutingHandler(PublicAddressGuard.CreateManualRedirectHandler));
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://8.8.8.8/page");
        ProxyRoute.Via(proxy.Url).ApplyTo(request);

        var ex = await Assert.ThrowsAsync<SourceProxyException>(() => invoker.SendAsync(request, CancellationToken.None));
        Assert.Equal(SourceProxyFailure.Rejected, ex.Failure);
    }

    [Fact]
    public async Task A_refused_destination_stays_a_refused_destination()
    {
        await using var proxy = new FakeConnectProxy(200);
        using var invoker = new HttpMessageInvoker(new ProxyRoutingHandler(PublicAddressGuard.CreateManualRedirectHandler));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://10.0.0.5/secret");
        ProxyRoute.Via(proxy.Url).ApplyTo(request);

        var ex = await Assert.ThrowsAnyAsync<HttpRequestException>(() => invoker.SendAsync(request, CancellationToken.None));
        Assert.IsNotType<SourceProxyException>(ex);
        Assert.Empty(proxy.Requests);
    }

    [Fact]
    public async Task A_stopped_proxy_sends_nothing_anywhere_else()
    {
        // The direct route would answer; a proxied request must never fall back to it.
        var build = new RecordingBuild();
        using var invoker = new HttpMessageInvoker(new ProxyRoutingHandler(proxy =>
            proxy is null ? build.Build(null) : PublicAddressGuard.CreateManualRedirectHandler(proxy)));

        await Assert.ThrowsAsync<SourceProxyException>(() => SendAsync(invoker, ProxyRoute.Via(new Uri("http://127.0.0.1:1"))));
        Assert.Empty(build.Built);
    }

    [Fact]
    public async Task Disposing_the_router_disposes_its_inner_handlers()
    {
        var inner = new List<StubHandler>();
        var router = new ProxyRoutingHandler(proxy =>
        {
            var handler = new StubHandler(proxy);
            inner.Add(handler);
            return handler;
        });
        using (var invoker = new HttpMessageInvoker(router))
        {
            await SendAsync(invoker, null);
            await SendAsync(invoker, ProxyRoute.Via(new Uri("http://192.168.1.20:3128")));
        }

        Assert.Equal(2, inner.Count);
        Assert.All(inner, handler => Assert.True(handler.Disposed));
    }

    [Fact]
    public async Task The_tag_handler_resolves_only_requests_without_a_route()
    {
        var settings = new MemorySettings();
        settings.Values[SettingKeys.SourceProxy] = """{"proxies":[{"id":"default","url":"http://192.168.1.20:3128"}],"sources":{"mangafire":"default"}}""";
        var seen = new List<ProxyRoute>();
        var tag = new SourceProxyTagHandler(new SourceProxyResolver(settings), "mangafire")
        {
            InnerHandler = new CallbackHandler(request => seen.Add(ProxyRoute.Of(request))),
        };
        using var invoker = new HttpMessageInvoker(tag);

        await SendAsync(invoker, null);
        var pinned = ProxyRoute.Via(new Uri("http://10.0.0.9:8888"));
        await SendAsync(invoker, pinned);

        Assert.Equal("http://192.168.1.20:3128", seen[0].Fingerprint);
        Assert.Equal(pinned, seen[1]);
    }

    private sealed class CallbackHandler(Action<HttpRequestMessage> onSend) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            onSend(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") });
        }
    }

    private sealed class NoCooldown : Maki.Core.Http.IDownloadCooldown
    {
        public TimeSpan Remaining(string sourceName) => TimeSpan.Zero;
        public Task WaitAsync(string sourceName, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    [Fact]
    public async Task Every_page_of_a_chapter_goes_out_on_the_source_route()
    {
        var settings = new MemorySettings();
        settings.Values[SettingKeys.SourceProxy] = """{"proxies":[{"id":"default","url":"http://192.168.1.20:3128"}],"sources":{"mangafire":"default"}}""";
        var seen = new List<ProxyRoute>();
        var handler = new CallbackHandler(request =>
        {
            lock (seen)
            {
                seen.Add(ProxyRoute.Of(request));
            }
        });
        var downloader = new Maki.Core.Download.PageDownloader(
            new SingleClientFactory(handler), new NoCooldown(), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Maki.Core.Download.PageDownloader>.Instance,
            new SourceProxyResolver(settings));
        var pages = new Maki.Core.Sources.ChapterPages(
            [.. Enumerable.Range(1, 5).Select(i => new Maki.Core.Sources.PageRequest($"https://8.8.8.8/{i}.jpg"))]);
        var dir = Path.Combine(Path.GetTempPath(), "maki-proxy-pages-" + Guid.NewGuid().ToString("N"));
        try
        {
            await downloader.DownloadAsync(pages, "mangafire", dir);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }

        Assert.Equal(5, seen.Count);
        Assert.All(seen, route => Assert.Equal("http://192.168.1.20:3128", route.Fingerprint));
    }

    [Theory]
    [InlineData("net::ERR_PROXY_CONNECTION_FAILED at https://example.test", SourceProxyFailure.Unreachable)]
    [InlineData("net::ERR_TUNNEL_CONNECTION_FAILED at https://example.test", SourceProxyFailure.Rejected)]
    [InlineData("net::ERR_PROXY_AUTH_UNSUPPORTED", SourceProxyFailure.Rejected)]
    [InlineData("net::ERR_NO_SUPPORTED_PROXIES", SourceProxyFailure.Rejected)]
    [InlineData("net::ERR_NAME_NOT_RESOLVED", null)]
    [InlineData(null, null)]
    public void Chromium_errors_name_proxy_failures_only(string? message, SourceProxyFailure? expected) =>
        Assert.Equal(expected, SourceProxyException.FromChromiumError(message));

    private sealed class FlareAnswer(HttpStatusCode status, string body) : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.OK)]
    public async Task FlareSolverr_failing_at_the_proxy_is_a_proxy_failure(HttpStatusCode status)
    {
        var client = new FlareSolverrClient(new FlareAnswer(status,
            """{"status":"error","message":"Error: Error solving the challenge. Message: unknown error: net::ERR_PROXY_CONNECTION_FAILED"}"""));

        var ex = await Assert.ThrowsAsync<SourceProxyException>(() =>
            client.GetAsync("http://flaresolverr.test:8191", "https://example.test/", ProxyRoute.Via(new Uri("http://192.0.2.1:3128"))));

        Assert.Equal(SourceProxyFailure.Unreachable, ex.Failure);
    }

    [Fact]
    public async Task FlareSolverr_failing_without_a_proxy_stays_a_flaresolverr_failure()
    {
        var client = new FlareSolverrClient(new FlareAnswer(HttpStatusCode.InternalServerError,
            """{"status":"error","message":"net::ERR_PROXY_CONNECTION_FAILED"}"""));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync("http://flaresolverr.test:8191", "https://example.test/", ProxyRoute.Direct));

        Assert.IsNotType<SourceProxyException>(ex);
    }
}
