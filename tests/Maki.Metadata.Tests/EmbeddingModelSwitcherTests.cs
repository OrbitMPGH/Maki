using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Maki.Core.Configuration;
using Maki.Metadata.Embedding;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Maki.Metadata.Tests;

public class EmbeddingModelSwitcherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "maki-switcher-" + Guid.NewGuid().ToString("N"));
    private readonly BlockableSettings _settings = new();
    private readonly EmbeddingIndexStatus _status = new();
    private readonly RecordingHandler _handler = new();
    private readonly EmbeddingOptions _options;

    public EmbeddingModelSwitcherTests()
    {
        Directory.CreateDirectory(_dir);
        _options = new EmbeddingOptions(
            _dir, Path.Combine(_dir, "embeddings.db"), _dir, EmbeddingModelProfile.Base);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Asking_for_the_model_already_in_use_does_nothing()
    {
        var switcher = Switcher();

        var start = switcher.Start("base");

        Assert.False(start.Started);
        Assert.Equal("install.embeddingModel.alreadyOn", start.Reason);
    }

    [Fact]
    public async Task Turning_embeddings_off_disables_them_without_touching_the_network()
    {
        var switcher = Switcher();

        Assert.True(switcher.Start("off").Started);
        await Settled(switcher);

        Assert.False(_options.Enabled);
        Assert.Equal("off", switcher.CurrentModel);
        Assert.Null(switcher.LastError);
        Assert.Empty(_handler.Requests);
        Assert.Equal("off", _settings.Values[SettingKeys.RecommendationsEmbeddingModel]);
    }

    [Fact]
    public async Task Turning_embeddings_back_on_keeps_vectors_the_file_already_holds()
    {
        SeedLocalIndex(rows: 1500, generatedAt: DateTime.UtcNow);
        PlaceModelFiles();
        _handler.Manifest = Manifest(rows: 1500, generatedAt: DateTime.UtcNow.AddDays(-1));
        _options.Enabled = false;
        var switcher = Switcher();

        Assert.True(switcher.Start("base").Started);
        await Settled(switcher);

        Assert.True(_options.Enabled);
        Assert.DoesNotContain(_handler.Requests, r => r.EndsWith(".zst", StringComparison.Ordinal));
        Assert.NotEqual("install.prebuiltIndex.current", switcher.LastError);
    }

    [Fact]
    public async Task A_failed_switch_reports_a_catalogue_key_not_an_exception_message()
    {
        _options.Enabled = false;
        var switcher = Switcher();

        Assert.True(switcher.Start("base").Started);
        await Settled(switcher);

        Assert.Equal("install.embeddingModel.switchFailed", switcher.LastError);
    }

    [Fact]
    public async Task A_second_switch_while_one_runs_is_refused()
    {
        var release = new TaskCompletionSource();
        _settings.Block = release.Task;
        var switcher = Switcher();

        Assert.True(switcher.Start("off").Started);
        var second = switcher.Start("off");
        release.SetResult();
        await Settled(switcher);

        Assert.False(second.Started);
        Assert.Equal("install.embeddingModel.switchInProgress", second.Reason);
    }

    private EmbeddingModelSwitcher Switcher()
    {
        var factory = new StubHttpClientFactory(_handler);
        var modelStore = new EmbeddingModelStore(factory, _options, NullLogger<EmbeddingModelStore>.Instance);
        var embedder = new TextEmbedder(_options, modelStore, NullLogger<TextEmbedder>.Instance);
        var cache = new VectorIndexCache(
            _options,
            new Maki.Metadata.MangaBaka.MangaBakaDumpOptions(Path.Combine(_dir, "dump.db"), _dir),
            NullLogger<VectorIndexCache>.Instance);
        var prebuilt = new PrebuiltIndexInstaller(
            factory, _options, new EmbeddingStore(_options), cache, _status, _settings,
            NullLogger<PrebuiltIndexInstaller>.Instance);
        return new EmbeddingModelSwitcher(
            _options, modelStore, embedder, cache, prebuilt, _status, _settings,
            NullLogger<EmbeddingModelSwitcher>.Instance);
    }

    private static async Task Settled(EmbeddingModelSwitcher switcher)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (switcher.Switching && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.False(switcher.Switching, "the switch did not finish");
    }

    private void SeedLocalIndex(int rows, DateTime generatedAt)
    {
        var store = new EmbeddingStore(_options);
        store.EnsureSchema();
        var batch = new List<(long, string, float[])>(rows);
        for (var i = 0; i < rows; i++)
        {
            var vec = new float[_options.Dimensions];
            vec[i % vec.Length] = 1f;
            batch.Add((i + 1, $"h{i}", vec));
        }

        store.UpsertBatch(batch);
        store.SetModelVersion(_options.ModelVersion);
        _settings.Values[SettingKeys.RecommendationsPrebuiltGeneratedAt] =
            generatedAt.ToString("O", CultureInfo.InvariantCulture);
    }

    // Just big enough to pass the model store's size check, so no download is attempted.
    private void PlaceModelFiles()
    {
        Directory.CreateDirectory(_options.ModelDirectory);
        using (var model = File.Create(_options.ModelPath))
        {
            model.SetLength(21_000_000);
        }

        using var vocab = File.Create(_options.VocabPath);
        vocab.SetLength(200_000);
    }

    private byte[] Manifest(int rows, DateTime generatedAt) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            modelVersion = _options.ModelVersion,
            dimensions = _options.Dimensions,
            rowCount = rows,
            generatedAt,
            sizeBytes = 1000,
            url = "https://example.test/embeddings.db.zst",
        }));

    private sealed class BlockableSettings : FakeAppSettings, IAppSettings
    {
        public Task? Block { get; set; }

        async Task IAppSettings.SetAsync(string key, string? value, CancellationToken ct)
        {
            if (Block is not null)
            {
                await Block;
            }

            await SetAsync(key, value, ct);
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly List<string> _requests = [];

        public IReadOnlyList<string> Requests
        {
            get
            {
                lock (_requests)
                {
                    return [.. _requests];
                }
            }
        }

        public byte[]? Manifest { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_requests)
            {
                _requests.Add(request.RequestUri!.AbsoluteUri);
            }

            var isManifest = request.RequestUri!.AbsoluteUri.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            return Task.FromResult(isManifest && Manifest is not null
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Manifest) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
