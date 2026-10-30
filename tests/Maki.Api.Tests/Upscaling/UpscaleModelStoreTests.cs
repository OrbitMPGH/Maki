using System.Net;
using System.Text.Json;
using Maki.Api.Configuration;
using Maki.Api.Services.Upscaling;
using Maki.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests.Upscaling;

public class UpscaleModelStoreTests : IDisposable
{
    private const string ChecksumKey = "error.upscaling.checksumMismatch";
    private const string DownloadKey = "error.upscaling.downloadFailed";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "maki-upscale-" + Guid.NewGuid().ToString("N"));
    private readonly string _configDir;
    private readonly string _serveDir;
    private readonly FakeAppSettings _settings = new();

    public UpscaleModelStoreTests()
    {
        _configDir = Path.Combine(_root, "config");
        _serveDir = Path.Combine(_root, "serve");
        Directory.CreateDirectory(_serveDir);
        File.Copy(UpscaleFixture.IdentityModelPath, Path.Combine(_serveDir, "span-s-2x.onnx"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string WriteManifest(string? sha256 = null, string file = "span-s-2x.onnx", string id = "span-s", int version = 1)
    {
        var path = Path.Combine(_serveDir, "manifest.json");
        var model = Path.Combine(_serveDir, "span-s-2x.onnx");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            version,
            models = new[]
            {
                new { id, file, sha256 = sha256 ?? UpscaleFixture.Sha256Of(model), bytes = new FileInfo(model).Length }
            }
        }));
        return path;
    }

    private UpscaleModelStore NewStore(HttpMessageHandler? handler = null) =>
        new(new FakeHttpClientFactory(handler ?? new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))),
            new AppPaths(_configDir), _settings, new UpscaleModelCatalog(), NullLogger<UpscaleModelStore>.Instance);

    private string ModelFile => Path.Combine(_configDir, "models", "upscale", "span-s-2x.onnx");

    [Fact]
    public async Task A_missing_model_downloads_from_a_manifest_path_and_becomes_ready()
    {
        _settings.Set(SettingKeys.UpscalingManifestUrl, WriteManifest());
        var store = NewStore();
        Assert.Equal(UpscaleModelState.Missing, store.GetStatus(UpscaleModelId.SpanS).State);
        Assert.False(store.IsReady(UpscaleModelId.SpanS));

        var status = await store.DownloadAsync(UpscaleModelId.SpanS);

        Assert.Equal(UpscaleModelState.Ready, status.State);
        Assert.Null(status.ErrorKey);
        Assert.Equal(new FileInfo(UpscaleFixture.IdentityModelPath).Length, status.Bytes);
        Assert.True(store.IsReady(UpscaleModelId.SpanS));
        Assert.Equal(ModelFile, store.PathOf(UpscaleModelId.SpanS));
        Assert.Equal(UpscaleFixture.Sha256Of(UpscaleFixture.IdentityModelPath), UpscaleFixture.Sha256Of(ModelFile));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(ModelFile)!, "*.partial"));
        Assert.Equal(UpscaleModelState.Missing, store.GetStatus(UpscaleModelId.RealEsrganAv3).State);
    }

    [Fact]
    public async Task A_file_url_manifest_works_too()
    {
        _settings.Set(SettingKeys.UpscalingManifestUrl, new Uri(WriteManifest()).AbsoluteUri);
        var store = NewStore();

        var status = await store.DownloadAsync(UpscaleModelId.SpanS);

        Assert.Equal(UpscaleModelState.Ready, status.State);
        Assert.True(File.Exists(ModelFile));
    }

    [Fact]
    public async Task A_wrong_checksum_deletes_the_file_and_fails_with_the_checksum_key()
    {
        _settings.Set(SettingKeys.UpscalingManifestUrl, WriteManifest(sha256: new string('0', 64)));
        var store = NewStore();

        var status = await store.DownloadAsync(UpscaleModelId.SpanS);

        Assert.Equal(UpscaleModelState.Failed, status.State);
        Assert.Equal(ChecksumKey, status.ErrorKey);
        Assert.False(File.Exists(ModelFile));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(ModelFile)!));
        Assert.False(store.IsReady(UpscaleModelId.SpanS));
    }

    [Fact]
    public async Task A_failed_model_can_be_downloaded_again_once_the_manifest_is_fixed()
    {
        _settings.Set(SettingKeys.UpscalingManifestUrl, WriteManifest(sha256: new string('0', 64)));
        var store = NewStore();
        await store.DownloadAsync(UpscaleModelId.SpanS);

        WriteManifest();
        var status = await store.DownloadAsync(UpscaleModelId.SpanS);

        Assert.Equal(UpscaleModelState.Ready, status.State);
        Assert.Null(status.ErrorKey);
    }

    [Theory]
    [InlineData("other-model", "span-s-2x.onnx", 1)]
    [InlineData("span-s", "..\\evil.onnx", 1)]
    [InlineData("span-s", "../evil.onnx", 1)]
    [InlineData("span-s", "..", 1)]
    [InlineData("span-s", ".", 1)]
    [InlineData("span-s", ".span-s-2x.onnx", 1)]
    [InlineData("span-s", "span-s-2x.onnx", 2)]
    public async Task A_manifest_that_does_not_list_the_model_cleanly_fails_the_download(string id, string file, int version)
    {
        _settings.Set(SettingKeys.UpscalingManifestUrl, WriteManifest(file: file, id: id, version: version));
        var store = NewStore();

        var status = await store.DownloadAsync(UpscaleModelId.SpanS);

        Assert.Equal(UpscaleModelState.Failed, status.State);
        Assert.Equal(DownloadKey, status.ErrorKey);
    }

    [Fact]
    public async Task An_unreadable_manifest_fails_with_the_download_key()
    {
        _settings.Set(SettingKeys.UpscalingManifestUrl, Path.Combine(_serveDir, "nope.json"));
        var store = NewStore();

        var status = await store.DownloadAsync(UpscaleModelId.SpanS);

        Assert.Equal(UpscaleModelState.Failed, status.State);
        Assert.Equal(DownloadKey, status.ErrorKey);
    }

    [Fact]
    public async Task Delete_returns_a_ready_model_to_missing()
    {
        _settings.Set(SettingKeys.UpscalingManifestUrl, WriteManifest());
        var store = NewStore();
        await store.DownloadAsync(UpscaleModelId.SpanS);

        var status = await store.DeleteAsync(UpscaleModelId.SpanS);

        Assert.Equal(UpscaleModelState.Missing, status.State);
        Assert.False(File.Exists(ModelFile));
        Assert.False(store.IsReady(UpscaleModelId.SpanS));
    }

    [Fact]
    public async Task A_model_already_on_disk_is_ready_at_startup_without_a_manifest()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ModelFile)!);
        File.Copy(UpscaleFixture.IdentityModelPath, ModelFile);

        var store = NewStore();

        Assert.True(store.IsReady(UpscaleModelId.SpanS));
        Assert.Equal(UpscaleModelState.Ready, store.GetStatus(UpscaleModelId.SpanS).State);
        Assert.Equal(UpscaleModelState.Missing, store.GetStatus(UpscaleModelId.RealEsrganAv3).State);

        var again = await store.DownloadAsync(UpscaleModelId.SpanS);
        Assert.Equal(UpscaleModelState.Ready, again.State);
    }

    [Fact]
    public async Task An_http_download_shows_downloading_and_a_second_call_joins_it()
    {
        var release = new TaskCompletionSource();
        var fileRequests = 0;
        var model = await File.ReadAllBytesAsync(Path.Combine(_serveDir, "span-s-2x.onnx"));
        var manifest = await File.ReadAllTextAsync(WriteManifest());
        var handler = new StubHandler(async request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url == "https://models.test/v1/manifest.json")
            {
                return Text(manifest);
            }

            Assert.Equal("https://models.test/v1/span-s-2x.onnx", url);
            Interlocked.Increment(ref fileRequests);
            await release.Task;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(model) };
        });
        _settings.Set(SettingKeys.UpscalingManifestUrl, "https://models.test/v1/manifest.json");
        var store = NewStore(handler);

        var first = store.DownloadAsync(UpscaleModelId.SpanS);
        await WaitFor(() => Volatile.Read(ref fileRequests) == 1);
        Assert.Equal(UpscaleModelState.Downloading, store.GetStatus(UpscaleModelId.SpanS).State);
        Assert.False(store.IsReady(UpscaleModelId.SpanS));

        var second = store.DownloadAsync(UpscaleModelId.SpanS);
        release.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.All(results, r => Assert.Equal(UpscaleModelState.Ready, r.State));
        Assert.Equal(1, fileRequests);
        Assert.True(File.Exists(ModelFile));
    }

    [Fact]
    public async Task An_http_error_on_the_model_file_fails_with_the_download_key()
    {
        var manifest = await File.ReadAllTextAsync(WriteManifest());
        var handler = new StubHandler(request =>
            request.RequestUri!.AbsoluteUri.EndsWith("manifest.json", StringComparison.Ordinal)
                ? Text(manifest)
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        _settings.Set(SettingKeys.UpscalingManifestUrl, "https://models.test/v1/manifest.json");
        var store = NewStore(handler);

        var status = await store.DownloadAsync(UpscaleModelId.SpanS);

        Assert.Equal(UpscaleModelState.Failed, status.State);
        Assert.Equal(DownloadKey, status.ErrorKey);
    }

    [Fact]
    public async Task Without_an_override_the_manifest_comes_from_the_github_release()
    {
        string? requested = null;
        var handler = new StubHandler(request =>
        {
            requested = request.RequestUri!.AbsoluteUri;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var store = NewStore(handler);

        var status = await store.DownloadAsync(UpscaleModelId.SpanS);

        Assert.Equal(UpscaleModelStore.DefaultManifestUrl, requested);
        Assert.Equal(DownloadKey, status.ErrorKey);
    }

    [Fact]
    public void Model_ids_parse_only_for_catalogue_entries()
    {
        Assert.True(UpscaleModelId.TryParse("RealESRGAN-AV3", out var id));
        Assert.Equal(UpscaleModelId.RealEsrganAv3, id);
        Assert.False(UpscaleModelId.TryParse("waifu2x-art", out _));
        Assert.False(UpscaleModelId.TryParse("bogus", out _));
        Assert.False(UpscaleModelId.TryParse(null, out _));
        Assert.Equal(2, new UpscaleModelCatalog().All.Count);
    }

    [Fact]
    public async Task Options_apply_defaults_and_drop_unknown_models()
    {
        var defaults = await UpscaleOptions.LoadAsync(_settings, default);
        Assert.False(defaults.Enabled);
        Assert.Empty(defaults.Models);
        Assert.Equal(UpscaleOptions.DefaultThreads, defaults.Threads);
        Assert.Equal(1024, defaults.CacheMb);
        Assert.Null(defaults.ManifestUrl);

        _settings.Set(SettingKeys.UpscalingEnabled, "true")
            .Set(SettingKeys.UpscalingModels, "span-s, nope ,span-s,realesrgan-av3")
            .Set(SettingKeys.UpscalingCacheMb, "5");
        var set = await UpscaleOptions.LoadAsync(_settings, default);
        Assert.True(set.Enabled);
        Assert.Equal([UpscaleModelId.SpanS, UpscaleModelId.RealEsrganAv3], set.Models);
        Assert.Equal(UpscaleOptions.MinCacheMb, set.CacheMb);
    }

    private static HttpResponseMessage Text(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static async Task WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "Timed out waiting for the condition.");
            await Task.Delay(10);
        }
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(UpscaleModelStore.HttpClientName, name);
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this(request => Task.FromResult(respond(request)))
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            respond(request);
    }
}
