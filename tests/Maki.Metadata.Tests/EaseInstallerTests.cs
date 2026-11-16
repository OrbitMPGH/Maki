using System.Net;
using System.Text;
using System.Text.Json;
using Maki.Core.Configuration;
using Maki.Metadata.Ease;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZstdSharp;

namespace Maki.Metadata.Tests;

/// <summary>
/// The download path for <c>ease-table.db</c>: the guards it shares with the co-read installer,
/// the fold refusal it shares with the taste vectors, and the kill switch.
/// </summary>
public class EaseInstallerTests : IDisposable
{
    private readonly string _dir;
    private readonly string _tablePath;
    private readonly FakeAppSettings _settings = new();
    private readonly StubHandler _handler = new();

    public EaseInstallerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "maki-ease-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _tablePath = Path.Combine(_dir, "ease-table.db");
    }

    [Fact]
    public async Task Installs_AValidTable_AndTheCacheReadsIt()
    {
        Publish(Build());
        var cache = Cache();

        Assert.Null(await cache.GetAsync());
        var result = await Installer(cache).InstallAsync(ct: CancellationToken.None);

        Assert.True(result.Installed, result.Reason);
        Assert.Equal(EaseInstaller.MinWeights, result.WeightCount);
        Assert.NotNull(_settings.Values.GetValueOrDefault(SettingKeys.RecommendationsEaseGeneratedAt));
        var table = await cache.GetAsync();
        Assert.NotNull(table);
        Assert.Equal(EaseInstaller.MinWeights, table.WeightCount);
    }

    [Fact]
    public async Task TheKillSwitch_StopsTheDownload_BeforeAnyRequest()
    {
        Publish(Build());
        await _settings.SetAsync(SettingKeys.RecommendationsEase, "false");

        var result = await Installer().InstallAsync(ct: CancellationToken.None);

        Assert.False(result.Installed);
        Assert.Equal("install.ease.disabled", result.Reason);
        Assert.Equal(0, _handler.Requests);
        Assert.False(File.Exists(_tablePath));
    }

    [Fact]
    public async Task AMissingManifest_IsTheNormalState_NotAnError()
    {
        var result = await Installer().InstallAsync(ct: CancellationToken.None);

        Assert.False(result.Installed);
        Assert.Equal("install.ease.manifestUnavailable", result.Reason);
    }

    [Fact]
    public async Task AFoldLimitedManifest_IsRefused_WithoutDownloading()
    {
        Publish(Build(), manifestFold: "1,2,3");

        var result = await Installer().InstallAsync(ct: CancellationToken.None);

        Assert.Equal("install.ease.evaluationBuild", result.Reason);
        Assert.Equal(1, _handler.Requests);
    }

    [Fact]
    public async Task AFileThatDoesNotSayWhoItWasTrainedOn_IsRefused()
    {
        Publish(Build(trainingFold: ""));

        var result = await Installer().InstallAsync(ct: CancellationToken.None);

        Assert.False(result.Installed);
        Assert.Contains("which readers", result.Reason);
        Assert.False(File.Exists(_tablePath));
    }

    [Fact]
    public async Task AFoldLimitedFile_IsRefused_EvenWhenTheManifestSaysAll()
    {
        Publish(Build(trainingFold: "1,2,3"));

        var result = await Installer().InstallAsync(ct: CancellationToken.None);

        Assert.False(result.Installed);
        Assert.Contains("fold-limited", result.Reason);
        Assert.False(File.Exists(_tablePath));
    }

    [Fact]
    public async Task AFileHoldingPerUserReadingRows_IsRefused()
    {
        Publish(Build(extraTable: "CREATE TABLE user_entry (user_id INTEGER, media_id INTEGER, status TEXT)"));

        var result = await Installer().InstallAsync(ct: CancellationToken.None);

        Assert.False(result.Installed);
        Assert.Contains("per-user reading tables", result.Reason);
        Assert.False(File.Exists(_tablePath));
    }

    [Fact]
    public async Task AChecksumMismatch_IsRefused()
    {
        Publish(Build(), sha256: new string('0', 64));

        var result = await Installer().InstallAsync(ct: CancellationToken.None);

        Assert.Contains("checksum mismatch", result.Reason);
        Assert.False(File.Exists(_tablePath));
    }

    [Fact]
    public async Task ANewerSchema_IsRefused()
    {
        Publish(Build(), schemaVersion: EaseInstaller.SupportedSchemaVersion + 1);

        Assert.Equal("install.ease.schemaNewer", (await Installer().InstallAsync(ct: CancellationToken.None)).Reason);
    }

    [Fact]
    public async Task ATruncatedManifest_IsRefused()
    {
        Publish(Build(), advertised: 10);

        Assert.Equal("install.ease.truncated", (await Installer().InstallAsync(ct: CancellationToken.None)).Reason);
    }

    [Fact]
    public async Task ASecondRun_IsCurrent_UnlessForced()
    {
        Publish(Build());
        var installer = Installer();
        Assert.True((await installer.InstallAsync(ct: CancellationToken.None)).Installed);

        Assert.Equal("install.ease.current", (await installer.InstallAsync(ct: CancellationToken.None)).Reason);
        Assert.True((await installer.InstallAsync(force: true, ct: CancellationToken.None)).Installed);
    }

    [Fact]
    public void ValidateStaged_RefusesASmallFile_SelfWeightsAndEmptyWeights()
    {
        Assert.Contains("only 10 weights", Assert.Throws<InvalidOperationException>(
            () => EaseInstaller.ValidateStaged(Build(rows: 10))).Message);
        Assert.Contains("self-weights", Assert.Throws<InvalidOperationException>(
            () => EaseInstaller.ValidateStaged(Build(selfRows: 2))).Message);
        Assert.Contains("missing, zero or non-finite", Assert.Throws<InvalidOperationException>(
            () => EaseInstaller.ValidateStaged(Build(zeroRows: 2))).Message);
    }

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(1e300)]
    public void ValidateStaged_RefusesWeightsTheTableWouldDrop(double bad)
    {
        Assert.Contains("non-finite", Assert.Throws<InvalidOperationException>(
            () => EaseInstaller.ValidateStaged(Build(badRows: 2, badWeight: bad))).Message);
    }

    [Fact]
    public void ValidateStaged_RefusesAFileWithNoWeightTable()
    {
        var path = Path.Combine(_dir, "empty.db");
        using (var conn = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            conn.Open();
            Execute(conn, "CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL)");
        }

        Assert.Contains("'weight'", Assert.Throws<InvalidOperationException>(() => EaseInstaller.ValidateStaged(path)).Message);
    }

    private EaseCache Cache() => new(new EaseOptions(_tablePath, _dir), NullLogger<EaseCache>.Instance);

    private EaseInstaller Installer(EaseCache? cache = null) => new(
        new StubHttpClientFactory(_handler),
        new EaseOptions(_tablePath, _dir),
        cache ?? Cache(),
        _settings,
        NullLogger<EaseInstaller>.Instance);

    private string Build(
        long rows = EaseInstaller.MinWeights, string trainingFold = "all", string? extraTable = null,
        int selfRows = 0, int zeroRows = 0, int badRows = 0, double badWeight = 0)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db");
        using (var conn = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            conn.Open();
            Execute(conn, """
                CREATE TABLE weight (
                    seed_id INTEGER NOT NULL, item_id INTEGER NOT NULL, weight REAL,
                    PRIMARY KEY (seed_id, item_id)) WITHOUT ROWID;
                CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                """);
            if (extraTable is not null)
            {
                Execute(conn, extraTable);
            }

            using (var tx = conn.BeginTransaction())
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO weight VALUES ($s, $t, $w)";
                var s = cmd.Parameters.Add("$s", SqliteType.Integer);
                var t = cmd.Parameters.Add("$t", SqliteType.Integer);
                var w = cmd.Parameters.Add("$w", SqliteType.Real);
                for (var i = 0L; i < rows; i++)
                {
                    s.Value = (i / 100) + 1;
                    t.Value = 1_000_000 + (i % 100);
                    w.Value = i < zeroRows ? 0.0 : i < zeroRows + badRows ? badWeight : (i % 2 == 0 ? 0.01 : -0.01);
                    cmd.ExecuteNonQuery();
                }

                for (var i = 0; i < selfRows; i++)
                {
                    s.Value = 5_000_000 + i;
                    t.Value = 5_000_000 + i;
                    w.Value = 0.5;
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
            }

            using var meta = conn.CreateCommand();
            meta.CommandText = "INSERT INTO meta VALUES ('schemaVersion', '1'), ('trainingFold', $f), ('generatedAt', $g)";
            meta.Parameters.AddWithValue("$f", trainingFold);
            meta.Parameters.AddWithValue("$g", DateTime.UtcNow.ToString("O"));
            meta.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        return path;
    }

    private void Publish(
        string path, string manifestFold = "all", string? sha256 = null, int schemaVersion = 1, long? advertised = null)
    {
        using var compressor = new Compressor(3);
        var compressed = compressor.Wrap(File.ReadAllBytes(path)).ToArray();
        _handler.Artifact = compressed;
        _handler.Manifest = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            schemaVersion,
            weightCount = advertised ?? EaseInstaller.MinWeights,
            itemCount = 20000,
            trainedReaders = 20179,
            trainingFold = manifestFold,
            generatedAt = DateTime.UtcNow,
            sizeBytes = compressed.Length,
            sha256 = sha256 ?? Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(compressed)),
            url = "https://example.test/ease-table.db.zst",
        }));
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
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
        catch (UnauthorizedAccessException)
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

        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            var body = request.RequestUri!.AbsoluteUri.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? Manifest : Artifact;
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }
}
