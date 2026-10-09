using System.Net;
using Maki.Core.Download;
using Maki.Core.Http;
using Maki.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Core.Tests;

public class PublicAddressGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.8.9.10")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.10")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    [InlineData("192.0.0.8")]
    [InlineData("198.18.0.1")]
    [InlineData("198.19.255.254")]
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.250")]
    [InlineData("255.255.255.255")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("64:ff9b::a9fe:a9fe")]
    [InlineData("2002:c0a8:0101::1")] // 6to4 wrapping 192.168.1.1
    [InlineData("2002:0a00:0001::1")] // 6to4 wrapping 10.0.0.1
    [InlineData("2001::1")] // Teredo
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2")] // Teredo, real-shaped
    [InlineData("64:ff9b:1::a9fe:a9fe")] // local-use NAT64 wrapping 169.254.169.254
    public void Rejects_non_public_addresses(string address) =>
        Assert.False(PublicAddressGuard.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("100.128.0.1")]
    [InlineData("192.0.1.1")]
    [InlineData("198.20.0.1")]
    [InlineData("2606:4700::6810:84e5")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("2002:0808:0808::1")] // 6to4 wrapping 8.8.8.8
    public void Accepts_public_addresses(string address) =>
        Assert.True(PublicAddressGuard.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("https://img.example.com/1.jpg", true)]
    [InlineData("http://8.8.8.8/1.jpg", true)]
    [InlineData("ftp://img.example.com/1.jpg", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("http://localhost:8990/api", false)]
    [InlineData("http://admin.localhost/", false)]
    [InlineData("http://127.0.0.1/x.jpg", false)]
    [InlineData("http://[::1]/x.jpg", false)]
    [InlineData("http://[::ffff:192.168.1.1]/x.jpg", false)]
    public void Checks_scheme_and_literal_hosts(string url, bool allowed) =>
        Assert.Equal(allowed, PublicAddressGuard.IsAllowedUrl(new Uri(url)));

    [Fact]
    public async Task Handler_refuses_to_connect_to_loopback()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var client = new HttpClient(PublicAddressGuard.CreateHandler());
        var ex = await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => client.GetAsync($"http://localhost:{port}/"));
        Exception? e = ex;
        while (e != null && e is not BlockedDestinationException)
        {
            e = e.InnerException;
        }

        Assert.NotNull(e);
    }

    // IsConfiguredProxy reads the process-wide HttpClient.DefaultProxy, which is lazily initialised
    // once from HTTP(S)_PROXY and cached for the process's lifetime, so a test cannot reliably force
    // the real proxy branch in ConnectAsync to run without risking interference with every other test
    // in this assembly. EnsureTargetPublicAsync is what that branch delegates the actual validation
    // to, so it is exercised directly here instead: a fake proxy on loopback would reach this same
    // code, since IsConfiguredProxy only decides whether to call it.
    [Fact]
    public async Task EnsureTargetPublicAsync_refuses_a_target_resolving_to_a_private_address()
    {
        var ex = await Assert.ThrowsAsync<BlockedDestinationException>(
            () => PublicAddressGuard.EnsureTargetPublicAsync(new Uri("http://127.0.0.1/secret"), CancellationToken.None).AsTask());
        Assert.IsType<BlockedDestinationException>(ex);
    }

    private sealed class FixedProxy(Uri? via) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }
        public Uri? GetProxy(Uri destination) => via;
        public bool IsBypassed(Uri host) => via is null;
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    [Fact]
    public async Task Proxied_plain_http_requests_are_checked_one_by_one()
    {
        var inner = new OkHandler();
        using var client = new HttpClient(new ProxiedTargetGuardHandler(new FixedProxy(new Uri("http://proxy.test:3128")))
        {
            InnerHandler = inner
        });

        (await client.GetAsync("http://8.8.8.8/ok")).Dispose();
        await Assert.ThrowsAsync<BlockedDestinationException>(() => client.GetAsync("http://10.0.0.5/secret"));
        await Assert.ThrowsAsync<BlockedDestinationException>(() => client.GetAsync("http://127.0.0.1/secret"));
        Assert.Equal(1, inner.Calls);
    }

    private sealed class RedirectingHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add(request.RequestUri!);
            return Task.FromResult(respond(request.RequestUri!));
        }
    }

    private static HttpResponseMessage Redirect(string to, HttpStatusCode status = HttpStatusCode.Found)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Location = new Uri(to, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static HttpClient FollowingClient(HttpMessageHandler inner) =>
        new(new ProxiedTargetGuardHandler(new FixedProxy(new Uri("http://proxy.test:3128")), followRedirects: true)
        {
            InnerHandler = inner
        });

    [Fact]
    public async Task A_redirect_to_a_private_address_is_refused_on_a_proxied_client()
    {
        var inner = new RedirectingHandler(uri => uri.Host == "8.8.8.8" ? Redirect("http://10.0.0.5/secret") : new HttpResponseMessage(HttpStatusCode.OK));
        using var client = FollowingClient(inner);

        await Assert.ThrowsAsync<BlockedDestinationException>(() => client.GetAsync("http://8.8.8.8/start"));
        Assert.Equal([new Uri("http://8.8.8.8/start")], inner.Seen);
    }

    [Fact]
    public async Task A_redirect_to_localhost_is_refused_before_it_is_sent()
    {
        var inner = new RedirectingHandler(_ => Redirect("http://localhost/admin"));
        using var client = FollowingClient(inner);

        await Assert.ThrowsAsync<BlockedDestinationException>(() => client.GetAsync("http://8.8.8.8/start"));
        Assert.Single(inner.Seen);
    }

    [Fact]
    public async Task Public_redirects_are_followed_including_relative_ones()
    {
        var inner = new RedirectingHandler(uri => uri.AbsolutePath switch
        {
            "/start" => Redirect("/middle"),
            "/middle" => Redirect("http://8.8.4.4/end", HttpStatusCode.PermanentRedirect),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
        });
        using var client = FollowingClient(inner);

        using var response = await client.GetAsync("http://8.8.8.8/start");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["http://8.8.8.8/start", "http://8.8.8.8/middle", "http://8.8.4.4/end"], inner.Seen.Select(u => u.ToString()));
    }

    [Fact]
    public async Task A_cookie_follows_a_redirect_on_the_same_host_but_not_to_another_one()
    {
        var cookies = new List<string?>();
        var inner = new RedirectingHandler(uri => uri.AbsolutePath switch
        {
            "/start" => Redirect("/same"),
            "/same" => Redirect("http://8.8.4.4/other"),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
        });
        using var client = FollowingClient(new CapturingHandler(inner, cookies));
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://8.8.8.8/start");
        request.Headers.TryAddWithoutValidation("Cookie", "session=1");

        using var response = await client.SendAsync(request);

        Assert.Equal(["session=1", "session=1", null], cookies);
    }

    private sealed class CapturingHandler(HttpMessageHandler inner, List<string?> cookies) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            cookies.Add(request.Headers.TryGetValues("Cookie", out var values) ? string.Join("; ", values) : null);
            return base.SendAsync(request, ct);
        }
    }

    [Fact]
    public async Task A_redirect_loop_stops_after_five_hops()
    {
        var inner = new RedirectingHandler(_ => Redirect("http://8.8.8.8/again"));
        using var client = FollowingClient(inner);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://8.8.8.8/start"));
        Assert.Equal(6, inner.Seen.Count);
    }

    [Fact]
    public async Task Redirects_are_left_alone_unless_the_handler_was_asked_to_follow_them()
    {
        var inner = new RedirectingHandler(_ => Redirect("http://10.0.0.5/secret"));
        using var client = new HttpClient(new ProxiedTargetGuardHandler(new FixedProxy(new Uri("http://proxy.test:3128")))
        {
            InnerHandler = inner
        });

        using var response = await client.GetAsync("http://8.8.8.8/start");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Single(inner.Seen);
    }

    [Fact]
    public async Task Requests_that_do_not_go_through_a_proxy_are_left_to_the_connect_check()
    {
        var inner = new OkHandler();
        using var client = new HttpClient(new ProxiedTargetGuardHandler(new FixedProxy(null)) { InnerHandler = inner });

        (await client.GetAsync("http://10.0.0.5/direct")).Dispose();
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task EnsureTargetPublicAsync_refuses_a_non_http_target()
    {
        await Assert.ThrowsAsync<BlockedDestinationException>(
            () => PublicAddressGuard.EnsureTargetPublicAsync(new Uri("ftp://example.com/x"), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task EnsureTargetPublicAsync_refuses_a_null_target()
    {
        await Assert.ThrowsAsync<BlockedDestinationException>(
            () => PublicAddressGuard.EnsureTargetPublicAsync(null, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task EnsureTargetPublicAsync_allows_a_public_literal_address()
    {
        await PublicAddressGuard.EnsureTargetPublicAsync(new Uri("http://8.8.8.8/x"), CancellationToken.None);
    }

    [Fact]
    public async Task PageDownloader_refuses_a_loopback_page_url()
    {
        var handler = new RecordingHandler();
        var downloader = new PageDownloader(
            new StubFactory(handler), new FakeCooldown(), TimeProvider.System, NullLogger<PageDownloader>.Instance);
        var pages = new ChapterPages([new PageRequest("http://127.0.0.1/admin/secret.jpg")]);

        var dir = Path.Combine(Path.GetTempPath(), "maki-pd-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Assert.ThrowsAsync<BlockedDestinationException>(() => downloader.DownloadAsync(pages, "fake", dir));
            Assert.Equal(0, handler.Calls);
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
