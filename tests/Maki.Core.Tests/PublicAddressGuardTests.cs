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
