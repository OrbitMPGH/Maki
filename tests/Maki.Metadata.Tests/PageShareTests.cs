using System.Net;
using System.Text;
using System.Text.Json;
using Maki.Core.Configuration;
using Maki.Metadata.Embedding;
using Maki.Metadata.PageShare;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZstdSharp;

namespace Maki.Metadata.Tests;

/// <summary>
/// The page-share table as the app gets it: <c>page-share.db</c> read into a
/// <see cref="PageFrequency"/>, the cache that holds it, and the installer's refusals and kill switch.
/// </summary>
public class PageShareTests : IDisposable
{
    private readonly string _dir;
    private readonly string _tablePath;
    private readonly FakeAppSettings _settings = new();
    private readonly StubHandler _handler = new();

    public PageShareTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "maki-page-share-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _tablePath = Path.Combine(_dir, "page-share.db");
    }

    [Fact]
    public void Load_ReadsTheArtifact_AndTheHarnessTextFormat()
    {
        var db = Build(rows: 3);
        var text = Path.Combine(_dir, "table.tsv");
        File.WriteAllText(text, "10\t0.4\n11\t0.2\nnot a row\n");

        var fromDb = PageFrequency.Load(db);
        var fromText = PageFrequency.Load(text);

        Assert.Equal(3, fromDb.Count);
        Assert.Equal(0.5, fromDb.ShareOf(1_000_000), 6);
        Assert.Equal(0, fromDb.ShareOf(42));
        Assert.NotNull(fromDb.GeneratedAt);
        Assert.Equal(2, fromText.Count);
        Assert.Equal(0.4, fromText.ShareOf(10), 6);
    }

    [Fact]
    public async Task TheCache_IsEmptyWithoutAFile_AndReadsOneSwappedIn()
    {
        var cache = Cache();
        Assert.Null(await cache.GetAsync());

        await cache.SwapDatabaseAsync(Build());

        var table = await cache.GetAsync();
        Assert.NotNull(table);
        Assert.Equal(PageShareInstaller.MinRows, table.Count);
        Assert.Same(table, await cache.GetAsync());
    }

    [Fact]
    public async Task Installs_AValidTable_AndTheCacheReadsIt()
    {
        Publish(Build());
        var cache = Cache();

        var result = await Installer(cache).InstallAsync(ct: CancellationToken.None);

        Assert.True(result.Installed, result.Reason);
        Assert.Equal(PageShareInstaller.MinRows, result.RowCount);
        Assert.NotNull(_settings.Values.GetValueOrDefault(SettingKeys.RecommendationsPageShareGeneratedAt));
        Assert.NotNull(await cache.GetAsync());
    }

    [Fact]
    public async Task TheKillSwitch_StopsTheDownload_BeforeAnyRequest()
    {
        Publish(Build());
        await _settings.SetAsync(SettingKeys.RecommendationsPageShare, "false");

        var result = await Installer().InstallAsync(ct: CancellationToken.None);

        Assert.False(result.Installed);
        Assert.Equal("install.pageShare.disabled", result.Reason);
        Assert.Equal(0, _handler.Requests);
        Assert.False(File.Exists(_tablePath));
    }

    [Fact]
    public async Task AFoldLimitedBuild_IsRefusedFromTheManifest_AndFromTheFile()
    {
        Publish(Build(), manifestFold: "readers 0/4");
        var fromManifest = await Installer().InstallAsync(ct: CancellationToken.None);
        Assert.Equal("install.pageShare.evaluationBuild", fromManifest.Reason);
        Assert.Equal(1, _handler.Requests);

        Assert.Contains("evaluation build", Assert.Throws<InvalidOperationException>(
            () => PageShareInstaller.ValidateStaged(Build(trainingFold: "ease 1,2,3"))).Message);
        Assert.Contains("which readers", Assert.Throws<InvalidOperationException>(
            () => PageShareInstaller.ValidateStaged(Build(trainingFold: ""))).Message);
    }

    [Fact]
    public void ValidateStaged_RefusesPerUserTables_ShortTables_AndBadShares()
    {
        Assert.Contains("per-user", Assert.Throws<InvalidOperationException>(() => PageShareInstaller.ValidateStaged(
            Build(extraTable: "CREATE TABLE user_entry (user_id INTEGER, media_id INTEGER, status TEXT)"))).Message);
        Assert.Contains("only 10", Assert.Throws<InvalidOperationException>(
            () => PageShareInstaller.ValidateStaged(Build(rows: 10))).Message);
        Assert.Contains("out-of-range", Assert.Throws<InvalidOperationException>(
            () => PageShareInstaller.ValidateStaged(Build(badShare: 1.5))).Message);
        Assert.Contains("out-of-range", Assert.Throws<InvalidOperationException>(
            () => PageShareInstaller.ValidateStaged(Build(badShare: double.NaN))).Message);
        Assert.Equal(PageShareInstaller.MinRows, PageShareInstaller.ValidateStaged(Build()));
    }

    private PageShareCache Cache() => new(new PageShareOptions(_tablePath, _dir), NullLogger<PageShareCache>.Instance);

    private PageShareInstaller Installer(PageShareCache? cache = null) => new(
        new StubHttpClientFactory(_handler),
        new PageShareOptions(_tablePath, _dir),
        cache ?? Cache(),
        _settings,
        NullLogger<PageShareInstaller>.Instance);

    /// <summary>A table in the shape <c>distribution/build-page-share.py</c> writes.</summary>
    internal static string Build(
        string dir, long rows = PageShareInstaller.MinRows, string trainingFold = "all", string? extraTable = null,
        double? badShare = null)
    {
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".db");
        using (var conn = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            conn.Open();
            Execute(conn, """
                CREATE TABLE share (series_id INTEGER PRIMARY KEY, share REAL);
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
                cmd.CommandText = "INSERT INTO share VALUES ($id, $share)";
                var id = cmd.Parameters.Add("$id", SqliteType.Integer);
                var share = cmd.Parameters.Add("$share", SqliteType.Real);
                for (var i = 0L; i < rows; i++)
                {
                    id.Value = 1_000_000 + i;
                    share.Value = i == 0 && badShare is { } bad ? (double.IsNaN(bad) ? DBNull.Value : bad) : 0.5 / (i + 1);
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

    private string Build(
        long rows = PageShareInstaller.MinRows, string trainingFold = "all", string? extraTable = null,
        double? badShare = null) => Build(_dir, rows, trainingFold, extraTable, badShare);

    private void Publish(string path, string manifestFold = "all")
    {
        using var compressor = new Compressor(3);
        var compressed = compressor.Wrap(File.ReadAllBytes(path)).ToArray();
        _handler.Artifact = compressed;
        _handler.Manifest = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            rowCount = PageShareInstaller.MinRows,
            readers = 3000,
            trainingFold = manifestFold,
            generatedAt = DateTime.UtcNow,
            sizeBytes = compressed.Length,
            sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(compressed)),
            url = "https://example.test/page-share.db.zst",
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
