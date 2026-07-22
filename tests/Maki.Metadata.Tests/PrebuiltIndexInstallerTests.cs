using System.Net;
using System.Text;
using System.Text.Json;
using Maki.Core.Configuration;
using Maki.Metadata.Embedding;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZstdSharp;

namespace Maki.Metadata.Tests;

public class PrebuiltIndexInstallerTests : IDisposable
{
    private const int Dimensions = 8;

    private readonly string _dir;
    private readonly string _vectorPath;
    private readonly FakeAppSettings _settings = new();
    private readonly EmbeddingIndexStatus _status = new();
    private readonly StubHandler _handler = new();

    public PrebuiltIndexInstallerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "maki-prebuilt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _vectorPath = Path.Combine(_dir, "embeddings.db");
    }

    [Fact]
    public async Task Installs_AValidArtifact()
    {
        Publish(rows: 2000, dimensions: Dimensions, modelVersion: EmbeddingModelProfile.Base.Version);

        var result = await Installer().InstallAsync(ct: CancellationToken.None);

        Assert.True(result.Installed, result.Reason);
        Assert.Equal(2000, result.RowCount);
        Assert.Equal(2000, Store().Count());
        // The install marker is what later runs compare against for freshness.
        Assert.NotNull(_settings.Values.GetValueOrDefault(SettingKeys.RecommendationsPrebuiltGeneratedAt));
    }

    [Fact]
    public async Task Refuses_AnArtifactBuiltForADifferentModel()
    {
        // The failure that hides: wrong-model vectors are dropped row by row at load, so search
        // would silently return nothing rather than reporting a problem.
        Publish(rows: 2000, dimensions: Dimensions, modelVersion: "some-other-model-v9");

        var result = await Installer().InstallAsync(ct: CancellationToken.None);

        Assert.False(result.Installed);
        Assert.Contains("different embedding model", result.Reason);
        Assert.False(File.Exists(_vectorPath));
    }

    [Fact]
    public async Task Refuses_AnArtifactOfTheWrongWidth()
    {
        Publish(rows: 2000, dimensions: Dimensions * 2, modelVersion: EmbeddingModelProfile.Base.Version);

        var result = await Installer().InstallAsync(ct: CancellationToken.None);

        Assert.False(result.Installed);
        Assert.Contains("dimensional", result.Reason);
        Assert.False(File.Exists(_vectorPath));
    }

    [Fact]
    public async Task Refuses_WhenTheChecksumDoesNotMatch()
    {
        Publish(rows: 2000, dimensions: Dimensions, modelVersion: EmbeddingModelProfile.Base.Version,
            sha256Override: new string('a', 64));

        var result = await Installer().InstallAsync(ct: CancellationToken.None);

        Assert.False(result.Installed);
        Assert.Contains("checksum mismatch", result.Reason);
        Assert.False(File.Exists(_vectorPath));
    }

    [Fact]
    public async Task Refuses_WhileAnIndexingPassIsRunning()
    {
        Publish(rows: 2000, dimensions: Dimensions, modelVersion: EmbeddingModelProfile.Base.Version);
        _status.Begin(); // a pass owns the database right now

        var result = await Installer().InstallAsync(ct: CancellationToken.None);

        Assert.False(result.Installed);
        Assert.Contains("indexing pass", result.Reason);
    }

    [Fact]
    public async Task Skips_WhenTheLocalIndexIsAlreadyCurrent()
    {
        Publish(rows: 2000, dimensions: Dimensions, modelVersion: EmbeddingModelProfile.Base.Version);
        var installer = Installer();
        Assert.True((await installer.InstallAsync(ct: CancellationToken.None)).Installed);

        var second = await installer.InstallAsync(ct: CancellationToken.None);

        Assert.False(second.Installed);
        Assert.Contains("already current", second.Reason);
    }

    [Fact]
    public async Task Force_ReinstallsEvenWhenCurrent()
    {
        Publish(rows: 2000, dimensions: Dimensions, modelVersion: EmbeddingModelProfile.Base.Version);
        var installer = Installer();
        await installer.InstallAsync(ct: CancellationToken.None);

        var forced = await installer.InstallAsync(force: true, ct: CancellationToken.None);

        Assert.True(forced.Installed, forced.Reason);
    }

    [Fact]
    public async Task Force_StillRespectsCompatibility()
    {
        // "Download now" must not be a way to install an index this build cannot read.
        Publish(rows: 2000, dimensions: Dimensions, modelVersion: "some-other-model-v9");

        var result = await Installer().InstallAsync(force: true, ct: CancellationToken.None);

        Assert.False(result.Installed);
        Assert.Contains("different embedding model", result.Reason);
    }

    [Fact]
    public async Task Skips_WhenEmbeddingsAreOff()
    {
        // Prebuilt downloads are always on while embeddings are on; the only "off" is the model
        // itself being turned off, even on a forced request.
        Publish(rows: 2000, dimensions: Dimensions, modelVersion: EmbeddingModelProfile.Base.Version);

        var result = await Installer(enabled: false).InstallAsync(force: true, ct: CancellationToken.None);

        Assert.False(result.Installed);
        Assert.Contains("turned off", result.Reason);
        Assert.False(File.Exists(_vectorPath));
    }

    [Fact]
    public async Task Skips_WhenTheManifestIsUnreachable()
    {
        _handler.ManifestStatus = HttpStatusCode.NotFound;

        var result = await Installer().InstallAsync(ct: CancellationToken.None);

        Assert.False(result.Installed);
        Assert.Contains("manifest", result.Reason);
    }

    [Fact]
    public async Task Tolerates_AManifestWithAByteOrderMark()
    {
        // The publish script runs on Windows PowerShell, which is fond of BOMs.
        Publish(rows: 2000, dimensions: Dimensions, modelVersion: EmbeddingModelProfile.Base.Version, withBom: true);

        var result = await Installer().InstallAsync(ct: CancellationToken.None);

        Assert.True(result.Installed, result.Reason);
    }

    private EmbeddingOptions Options() =>
        new(_dir, _vectorPath, _dir, EmbeddingModelProfile.Base with { Dimensions = Dimensions });

    private EmbeddingStore Store() => new(Options());

    private PrebuiltIndexInstaller Installer(bool enabled = true)
    {
        var options = Options();
        options.Enabled = enabled;
        var cache = new VectorIndexCache(
            options,
            new MangaBaka.MangaBakaDumpOptions(Path.Combine(_dir, "dump.db"), _dir),
            NullLogger<VectorIndexCache>.Instance);
        return new PrebuiltIndexInstaller(
            new StubHttpClientFactory(_handler),
            options,
            new EmbeddingStore(options),
            cache,
            _status,
            _settings,
            NullLogger<PrebuiltIndexInstaller>.Instance);
    }

    /// <summary>Builds a compressed artifact + manifest and puts them behind the stub HTTP handler.</summary>
    private void Publish(
        int rows, int dimensions, string modelVersion, string? sha256Override = null, bool withBom = false)
    {
        var sourcePath = Path.Combine(_dir, $"artifact-{Guid.NewGuid():N}.db");
        var source = new EmbeddingStore(new EmbeddingOptions(_dir, sourcePath, _dir, EmbeddingModelProfile.Base with { Dimensions = dimensions }));
        source.EnsureSchema();
        var batch = new List<(long, string, float[])>();
        for (var i = 0; i < rows; i++)
        {
            var vec = new float[dimensions];
            vec[i % dimensions] = 1f;
            batch.Add((i + 1, $"h{i}", vec));
        }

        source.UpsertBatch(batch);
        SqliteConnection.ClearAllPools();

        var raw = File.ReadAllBytes(sourcePath);
        using var compressor = new Compressor(3);
        var compressed = compressor.Wrap(raw).ToArray();

        _handler.Artifact = compressed;
        var manifest = JsonSerializer.Serialize(new
        {
            modelVersion,
            dimensions,
            quantized = true,
            rowCount = rows,
            generatedAt = DateTime.UtcNow,
            fileName = "embeddings.db.zst",
            sizeBytes = compressed.Length,
            sha256 = sha256Override ?? Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(compressed)),
            url = "https://example.test/embeddings.db.zst",
        });

        _handler.Manifest = withBom
            ? Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(manifest)).ToArray()
            : Encoding.UTF8.GetBytes(manifest);
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

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public byte[]? Manifest { get; set; }

        public byte[]? Artifact { get; set; }

        public HttpStatusCode ManifestStatus { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var isManifest = request.RequestUri!.AbsoluteUri.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            if (isManifest)
            {
                if (ManifestStatus != HttpStatusCode.OK || Manifest is null)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Manifest),
                });
            }

            return Task.FromResult(Artifact is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Artifact) });
        }
    }
}
