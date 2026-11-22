using System.Net;
using Maki.Core.Http;
using Maki.Core.Http.Proxy;

namespace Maki.Core.Tests;

public class ProxyGuardTests
{
    // The handler's proxy set by hand, the way HttpClient.DefaultProxy would be: the CONNECT check
    // must not depend on which of the two named the proxy.
    private static HttpClient DefaultStyleClientVia(Uri proxy)
    {
        var handler = PublicAddressGuard.CreateHandler();
        handler.Proxy = new WebProxy(proxy);
        handler.UseProxy = true;
        return new HttpClient(handler);
    }

    [Fact]
    public async Task An_https_tunnel_through_a_lan_proxy_to_a_public_target_is_allowed()
    {
        await using var proxy = new FakeConnectProxy(status: 403);
        using var client = DefaultStyleClientVia(proxy.Url);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://8.8.8.8/"));

        Assert.Equal(HttpRequestError.ProxyTunnelError, ex.HttpRequestError);
        Assert.Equal(["CONNECT 8.8.8.8:443 HTTP/1.1"], proxy.Requests);
    }

    [Fact]
    public async Task An_explicit_proxy_handler_tunnels_to_public_targets()
    {
        await using var proxy = new FakeConnectProxy(status: 403);
        using var client = new HttpClient(PublicAddressGuard.CreateHandler(proxy.Url));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://8.8.4.4/x"));

        Assert.Equal(HttpRequestError.ProxyTunnelError, ex.HttpRequestError);
        Assert.Equal(["CONNECT 8.8.4.4:443 HTTP/1.1"], proxy.Requests);
    }

    [Theory]
    [InlineData("https://10.0.0.5/secret")]
    [InlineData("https://127.0.0.1/secret")]
    [InlineData("https://169.254.169.254/latest")]
    public async Task An_https_tunnel_to_a_private_target_is_refused_before_the_proxy_sees_it(string url)
    {
        await using var proxy = new FakeConnectProxy(status: 200);
        using var client = new HttpClient(PublicAddressGuard.CreateHandler(proxy.Url));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(url));

        Assert.IsType<BlockedDestinationException>(ex.InnerException);
        Assert.Empty(proxy.Requests);
    }

    [Fact]
    public async Task A_name_that_does_not_resolve_here_is_left_to_the_proxy()
    {
        await using var proxy = new FakeConnectProxy(status: 403);
        using var client = new HttpClient(PublicAddressGuard.CreateHandler(proxy.Url));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://maki-test.invalid/"));

        Assert.Equal(HttpRequestError.ProxyTunnelError, ex.HttpRequestError);
        Assert.Equal(["CONNECT maki-test.invalid:443 HTTP/1.1"], proxy.Requests);
    }

    [Fact]
    public async Task A_connect_to_an_address_that_is_not_the_proxy_gets_the_public_address_check()
    {
        await using var proxy = new FakeConnectProxy(status: 200);
        using var client = new HttpClient(PublicAddressGuard.CreateHandler());
        using var request = new HttpRequestMessage(HttpMethod.Connect, proxy.Url);
        request.Headers.Host = "8.8.8.8:443";

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));

        Assert.IsType<BlockedDestinationException>(ex.InnerException);
        Assert.Empty(proxy.Requests);
    }

    [Fact]
    public async Task Plain_http_through_an_explicit_proxy_checks_the_target()
    {
        await using var proxy = new FakeConnectProxy(status: 204);
        using var client = new HttpClient(PublicAddressGuard.CreateHandler(proxy.Url));

        using var ok = await client.GetAsync("http://8.8.8.8/page");
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        Assert.Equal(["GET http://8.8.8.8/page HTTP/1.1"], proxy.Requests);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(Uri Uri, ProxyRoute Route)> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request.RequestUri!, ProxyRoute.Of(request)));
            return Task.FromResult(respond(request));
        }
    }

    [Fact]
    public async Task A_redirect_hop_keeps_the_route_of_the_request_it_follows()
    {
        var route = ProxyRoute.Via(new Uri("http://192.168.1.20:3128"));
        var inner = new RecordingHandler(request =>
        {
            if (request.RequestUri!.Host == "8.8.8.8")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://8.8.4.4/cdn/1.jpg");
                return redirect;
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new HttpClient(new ProxiedTargetGuardHandler(followRedirects: true) { InnerHandler = inner });

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://8.8.8.8/page/1");
        route.ApplyTo(request);
        (await client.SendAsync(request)).Dispose();

        Assert.Equal(2, inner.Seen.Count);
        Assert.All(inner.Seen, seen => Assert.Equal(route, seen.Route));
    }

    [Fact]
    public async Task A_routed_plain_http_request_is_checked_against_its_own_proxy()
    {
        var route = ProxyRoute.Via(new Uri("http://192.168.1.20:3128"));
        var inner = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        // No handler-level proxy and (in tests) no environment proxy: only the route says it is proxied.
        using var client = new HttpClient(new ProxiedTargetGuardHandler() { InnerHandler = inner });

        using var request = new HttpRequestMessage(HttpMethod.Get, "http://10.0.0.5/secret");
        route.ApplyTo(request);

        await Assert.ThrowsAsync<BlockedDestinationException>(() => client.SendAsync(request));
        Assert.Empty(inner.Seen);
    }
}
