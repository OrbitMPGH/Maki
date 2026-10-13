using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Maki.Api.Tests;

[Collection(ConfigDirCollection.Name)]
public sealed class PrecompressedStaticFilesTests : IDisposable
{
    private static readonly string Script = string.Concat(Enumerable.Repeat("console.log('maki');\n", 200));
    private static readonly string Shell = "<!doctype html><title>shell</title>" + new string(' ', 1200);

    private readonly string _configDir;
    private readonly string _webRoot;
    private readonly string? _previousConfigDir;

    public PrecompressedStaticFilesTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "maki-static-" + Guid.NewGuid().ToString("N"));
        _webRoot = Path.Combine(_configDir, "www");
        Directory.CreateDirectory(Path.Combine(_webRoot, "assets"));
        _previousConfigDir = Environment.GetEnvironmentVariable("MAKI_CONFIG_DIR");
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _configDir);

        WriteWithSiblings("assets/app-abc123.js", Script);
        WriteWithSiblings("index.html", Shell);
        File.WriteAllText(Path.Combine(_webRoot, "assets", "tiny.js"), "x");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _previousConfigDir);
        try { Directory.Delete(_configDir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private void WriteWithSiblings(string relative, string content)
    {
        var path = Path.Combine(_webRoot, relative);
        var bytes = Encoding.UTF8.GetBytes(content);
        File.WriteAllBytes(path, bytes);

        using (var br = new MemoryStream())
        {
            using (var stream = new BrotliStream(br, CompressionLevel.SmallestSize, leaveOpen: true))
                stream.Write(bytes);
            File.WriteAllBytes(path + ".br", br.ToArray());
        }

        using var gz = new MemoryStream();
        using (var stream = new GZipStream(gz, CompressionLevel.SmallestSize, leaveOpen: true))
            stream.Write(bytes);
        File.WriteAllBytes(path + ".gz", gz.ToArray());
    }

    private HttpClient Client(WebApplicationFactory<Program> factory) =>
        factory.WithWebHostBuilder(b => b.UseEnvironment("Production").UseSetting(WebHostDefaults.WebRootKey, _webRoot)).CreateClient();

    private static async Task<HttpResponseMessage> Get(HttpClient client, string path, string? acceptEncoding)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (acceptEncoding is not null)
            request.Headers.AcceptEncoding.ParseAdd(acceptEncoding);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task An_asset_is_served_from_its_brotli_sibling_when_the_client_accepts_br()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = Client(factory);

        using var response = await Get(client, "/assets/app-abc123.js", "br");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["br"], response.Content.Headers.ContentEncoding);
        Assert.Contains("Accept-Encoding", response.Headers.Vary);
        Assert.Equal("public, max-age=31536000, immutable", response.Headers.CacheControl?.ToString());
        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            await File.ReadAllBytesAsync(Path.Combine(_webRoot, "assets", "app-abc123.js.br")),
            await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Gzip_is_used_when_br_is_not_accepted()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = Client(factory);

        using var response = await Get(client, "/assets/app-abc123.js", "gzip");

        Assert.Equal(["gzip"], response.Content.Headers.ContentEncoding);
        Assert.Equal(
            await File.ReadAllBytesAsync(Path.Combine(_webRoot, "assets", "app-abc123.js.gz")),
            await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Without_accept_encoding_the_plain_file_is_served_and_still_varies()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = Client(factory);

        using var response = await Get(client, "/assets/app-abc123.js", null);

        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Contains("Accept-Encoding", response.Headers.Vary);
        Assert.Equal(Script, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_file_with_no_sibling_is_served_plain_whatever_the_client_accepts()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = Client(factory);

        using var response = await Get(client, "/assets/tiny.js", "br");

        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.DoesNotContain("Accept-Encoding", response.Headers.Vary);
        Assert.Equal("x", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_sibling_older_than_its_source_is_ignored()
    {
        var path = Path.Combine(_webRoot, "assets", "stale-abc123.js");
        WriteWithSiblings("assets/stale-abc123.js", Script);
        File.SetLastWriteTimeUtc(path + ".br", File.GetLastWriteTimeUtc(path).AddMinutes(-5));
        File.SetLastWriteTimeUtc(path + ".gz", File.GetLastWriteTimeUtc(path).AddMinutes(-5));
        using var factory = new WebApplicationFactory<Program>();
        using var client = Client(factory);

        using var response = await Get(client, "/assets/stale-abc123.js", "br, gzip");

        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Equal(Script, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_not_modified_answer_names_no_encoding_but_still_varies()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = Client(factory);
        using var first = await Get(client, "/assets/app-abc123.js", "br");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/assets/app-abc123.js");
        request.Headers.AcceptEncoding.ParseAdd("br");
        request.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using var second = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(second.Content.Headers.ContentEncoding);
        Assert.Contains("Accept-Encoding", second.Headers.Vary);
    }

    [Fact]
    public async Task A_direct_request_for_a_sibling_is_a_plain_file_with_no_content_encoding()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = Client(factory);

        using var response = await Get(client, "/assets/app-abc123.js.gz", "gzip");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Equal(
            await File.ReadAllBytesAsync(Path.Combine(_webRoot, "assets", "app-abc123.js.gz")),
            await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("br", "br")]
    [InlineData(null, null)]
    public async Task The_spa_fallback_serves_index_html_and_keeps_it_revalidated(string? accept, string? encoding)
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = Client(factory);

        using var response = await Get(client, "/library", accept);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        Assert.Equal(encoding is null ? [] : [encoding], response.Content.Headers.ContentEncoding);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        var bytes = await response.Content.ReadAsByteArrayAsync();
        if (encoding is null)
        {
            Assert.Equal(Shell, Encoding.UTF8.GetString(bytes));
            return;
        }

        await using var brotli = new BrotliStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var reader = new StreamReader(brotli);
        Assert.Equal(Shell, await reader.ReadToEndAsync());
    }
}
