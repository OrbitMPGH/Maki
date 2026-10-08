using System.Net;
using System.Text.Json;
using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Metadata;
using Maki.Core.Sources;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// The cover proxy serves an upstream body from Maki's own origin under the reader's session. The
/// bytes, not the upstream's Content-Type, decide what goes out, and anything that is not a raster
/// image is refused: a source CDN answering HTML would otherwise run as Maki.
/// </summary>
public class SourceCoverProxyTests
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1];

    [Fact]
    public async Task Refuses_a_document_even_when_labelled_as_an_image()
    {
        var html = "<!doctype html><script>fetch('/api/v1/users')</script>"u8.ToArray();
        var (controller, _) = Build(Respond(HttpStatusCode.OK, html, "image/png"));

        var result = await controller.SourceCover("src", "https://cdn.src.test/a.png", CancellationToken.None);

        AssertRefused(result, "error.search.notAnImage");
    }

    [Fact]
    public async Task Serves_the_sniffed_type_and_ignores_the_declared_one()
    {
        var (controller, http) = Build(Respond(HttpStatusCode.OK, Jpeg, "text/html"));

        var result = await controller.SourceCover("src", "https://cdn.src.test/a", CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("image/jpeg", file.ContentType);
        Assert.Equal(Jpeg, file.FileContents);
        Assert.Equal("private,max-age=86400", http.Response.Headers.CacheControl.ToString());
        Assert.Equal("default-src 'none'; sandbox", http.Response.Headers.ContentSecurityPolicy.ToString());
    }

    [Fact]
    public async Task Refuses_a_body_over_the_cap()
    {
        var huge = new byte[10 * 1024 * 1024 + 1];
        Jpeg.CopyTo(huge, 0);
        var (controller, _) = Build(Respond(HttpStatusCode.OK, huge, "image/jpeg"));

        var result = await controller.SourceCover("src", "https://cdn.src.test/big.jpg", CancellationToken.None);

        AssertRefused(result, "error.search.coverUnavailable");
    }

    [Fact]
    public async Task Maps_an_upstream_error_to_a_bad_gateway()
    {
        var (controller, _) = Build(Respond(HttpStatusCode.NotFound, [], "text/html"));

        var result = await controller.SourceCover("src", "https://cdn.src.test/gone.jpg", CancellationToken.None);

        AssertRefused(result, "error.search.coverUnavailable");
    }

    [Fact]
    public async Task Maps_a_network_failure_to_a_bad_gateway()
    {
        var (controller, _) = Build((_, _) => throw new HttpRequestException("dns"));

        var result = await controller.SourceCover("src", "https://cdn.src.test/a.jpg", CancellationToken.None);

        AssertRefused(result, "error.search.coverUnavailable");
    }

    [Fact]
    public async Task Still_refuses_hosts_outside_the_source()
    {
        var (controller, _) = Build(Respond(HttpStatusCode.OK, Jpeg, "image/jpeg"));

        var result = await controller.SourceCover("src", "https://evil.test/a.jpg", CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("error.search.hostNotServed", JsonSerializer.Serialize(bad.Value));
    }

    private static void AssertRefused(IActionResult result, string key)
    {
        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, status.StatusCode);
        Assert.Contains(key, JsonSerializer.Serialize(status.Value));
    }

    private static Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> Respond(
        HttpStatusCode status, byte[] body, string contentType) =>
        (_, _) =>
        {
            var content = new ByteArrayContent(body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            return new HttpResponseMessage(status) { Content = content };
        };

    private static (SearchController Controller, DefaultHttpContext Http) Build(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> upstream)
    {
        var registry = new SourceRegistry([new FakeSource { Name = "src" }]);
        var settings = new FakeAppSettings();
        var http = new DefaultHttpContext();
        var controller = new SearchController(
            new TestLocalizer(),
            Array.Empty<IMetadataProvider>(),
            registry,
            new SourceAvailability(settings, registry),
            new TestCurrentUser(1),
            new UpstreamFactory(upstream),
            settings,
            NullLogger<SearchController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
        return (controller, http);
    }

    private sealed class UpstreamFactory(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> upstream)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(upstream));

        private sealed class Handler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> upstream)
            : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                Task.FromResult(upstream(request, ct));
        }
    }
}
