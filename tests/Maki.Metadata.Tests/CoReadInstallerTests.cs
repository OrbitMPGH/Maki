using System.Net;
using System.Text;
using System.Text.Json;
using Maki.Core.Configuration;
using Maki.Metadata.CoRead;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZstdSharp;

namespace Maki.Metadata.Tests;

/// <summary>
/// The download path for <c>coread-edges.db</c>. Mostly the same guards as the co-recommendation
/// installer, plus one that is not about correctness at all: the fetcher's working database holds
/// per-user reading rows, sits next to the artifact under a near-identical name, and must never be
/// installed even if somebody publishes it by mistake.
/// </summary>
public class CoReadInstallerTests : IDisposable
{
    private readonly string _dir;
    private readonly string _graphPath;
    private readonly FakeAppSettings _settings = new();
    private readonly StubHandler _handler = new();

    public CoReadInstallerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "maki-coread-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _graphPath = Path.Combine(_dir, "coread-edges.db");
    }

    [Fact]
    public async Task Installs_AValidArtifact()
    {
        Publish(pairs: 2000);

        var result = await Installer().InstallAsync(ct: CancellationToken.None);

        Assert.True(result.Installed, result.Reason);
        Assert.Equal(2000, result.PairCount);
        Assert.True(File.Exists(_graphPath));
        Assert.NotNull(_settings.Values.GetValueOrDefault(SettingKeys.RecommendationsCoReadGeneratedAt));
    }

    [Fact]
    public async Task InstalledGraph_IsImmediatelyReadable()
    {
        // The swap has to invalidate the cache too, or the channel keeps answering from whatever was
        // loaded before the download and the install looks like it did nothing.
        Publish(pairs: 2000);
        var cache = Cache();

        Assert.Null(await cache.GetAsync());
        Assert.True((await Installer(cache).InstallAsync(ct: CancellationToken.None)).Installed);

        var graph = await cache.GetAsync();
        Assert.NotNull(graph);
        Assert.Equal(2000 * 2, graph.EdgeCount);
    }

    [Fact]
    public async Task Refuses_AFileHoldingPerUserReadingRows()
    {
        // The guard that is not about correctness. coread-graph.db and coread-edges.db differ by
        // four characters and live in the same folder; the first holds one row per user per series.
        // Refusing it here cannot undo a mistaken publish, but it stops every install that would
        // otherwise download and keep a copy, and it makes the mistake loud rather than silent.
        PublishWorkingDatabase();

        var result = await Installer().InstallAsync(ct: CancellationToken.None);

        Assert.False(result.Installed);
        Assert.Contains("per-user reading tables", result.Reason);
        Assert.False(File.Exists(_graphPath));
    }

    [Fact]
    public async Task Refuses_AFileHoldingPerUserRows_EvenWhenItAlsoHasAValidPairTable()
    {
        // The likelier shape of the accident: the working database has a `cooccurrence` table that
        // an over-helpful export could rename, leaving something that passes every structural check
        // while still carrying user_entry alongside it. Order matters — the personal-data check runs
        // before the shape check for exactly this case.
        PublishWorkingDatabase(withPairTable: true, pairs: 2000);

        var result = await Installer().InstallAsync(force: true, ct: CancellationToken.None);

        Assert.False(result.Installed);
        Assert.Contains("per-user reading tables", result.Reason);
        Assert.False(File.Exists(_graphPath));
    }

    [Fact]
    public async Task Refuses_AnArtifactWithMissingOrNonPositiveStrengths()
    {
        // NULL is the case that occurs in practice: SQLite has no NaN and stores one as NULL, so a
        // strength that went wrong upstream arrives as a missing value rather than a poisoned one.
        // It is also the case a naive `strength <= 0` misses, since comparing against NULL yields
        // NULL and the row passes.
        Publish(pairs: 2000, nullStrengthRows: 2);

        var result = await Installer().InstallAsync(ct: CancellationToken.None);

        Assert.False(result.Installed);
        Assert.Contains("missing or non-positive", result.Reason);
        Assert.False(File.Exists(_graphPath));
    }

    [Fact]
    public async Task Refuses_AnArtifactWithZeroStrengths()
    {
        Publish(pairs: 2000, zeroStrengthRows: 3);

        var result = await Installer().InstallAsync(ct: CancellationToken.None);

        Assert.False(result.Installed);
        Assert.Contains("missing or non-positive", result.Reason);
    }

    [Fact]
    public async Task Refuses_AnArtifactWithSelfPairs()
    {
        Publish(pairs: 2000, selfPairRows: 3);

        var result = await Installer().InstallAsync(ct: CancellationToken.None);

        Assert.False(result.Installed);
        Assert.Contains("self-pair", result.Reason);
        Assert.False(File.Exists(_graphPath));
    }

    [Fact]
    public async Task Refuses_AnArtifactMuchSmallerThanAdvertised()
    {
        Publish(pairs: 2000, advertisedPairs: 100_000);

        var result = await Installer().InstallAsync(ct: CancellationToken.None);

        Assert.False(result.Installed);
        Assert.Contains("well short", result.Reason);
    }

    [Fact]
    public async Task Refuses_ANewerSchemaThanThisBuildReads()
    {
        Publish(pairs: 2000, schemaVersion: CoReadInstaller.SupportedSchemaVersion + 1);

        var result = await Installer().InstallAsync(force: true, ct: CancellationToken.None);

        Assert.False(result.Installed);
        Assert.Contains("newer schema", result.Reason);
        Assert.False(File.Exists(_graphPath));
    }

    [Fact]
    public async Task Refuses_WhenTheChecksumDoesNotMatch()
    {
        Publish(pairs: 2000, sha256Override: new string('a', 64));

        var result = await Installer().InstallAsync(ct: CancellationToken.None);

        Assert.False(result.Installed);
        Assert.Contains("checksum mismatch", result.Reason);
        Assert.False(File.Exists(_graphPath));
    }

    [Fact]
    public async Task Skips_WhenTheGraphIsAlreadyCurrent()
    {
        Publish(pairs: 2000);
        var installer = Installer();
        Assert.True((await installer.InstallAsync(ct: CancellationToken.None)).Installed);

        var second = await installer.InstallAsync(ct: CancellationToken.None);

        Assert.False(second.Installed);
        Assert.Contains("already current", second.Reason);
    }

    [Fact]
    public async Task Force_ReinstallsEvenWhenCurrent()
    {
        Publish(pairs: 2000);
        var installer = Installer();
        await installer.InstallAsync(ct: CancellationToken.None);

        Assert.True((await installer.InstallAsync(force: true, ct: CancellationToken.None)).Installed);
    }

    [Fact]
    public async Task Force_StillRespectsTheSafetyChecks()
    {
        // "Download now" must not be a way past the guard that keeps user data out.
        PublishWorkingDatabase();

        var result = await Installer().InstallAsync(force: true, ct: CancellationToken.None);

        Assert.False(result.Installed);
        Assert.Contains("per-user reading tables", result.Reason);
    }

    [Fact]
    public async Task Skips_WhenTheChannelIsTurnedOff()
    {
        Publish(pairs: 2000);
        _settings.Values[SettingKeys.RecommendationsCoRead] = "false";

        var result = await Installer().InstallAsync(force: true, ct: CancellationToken.None);

        Assert.False(result.Installed);
        Assert.Contains("turned off", result.Reason);
        Assert.False(File.Exists(_graphPath));
    }

    [Fact]
    public async Task ItsOwnSwitch_NotTheCoRecommendationOne()
    {
        // The two artifacts install independently; turning the vote graph off must not stop this
        // one arriving.
        Publish(pairs: 2000);
        _settings.Values[SettingKeys.RecommendationsCoGraph] = "false";

        Assert.True((await Installer().InstallAsync(ct: CancellationToken.None)).Installed);
    }

    [Fact]
    public async Task Skips_WhenNothingIsPublished()
    {
        // Today's normal state for every install. Has to be quiet, and leave no file.
        _handler.ManifestStatus = HttpStatusCode.NotFound;

        var result = await Installer().InstallAsync(ct: CancellationToken.None);

        Assert.False(result.Installed);
        Assert.Contains("manifest", result.Reason);
        Assert.False(File.Exists(_graphPath));
    }

    [Fact]
    public async Task Tolerates_AManifestWithAByteOrderMark()
    {
        Publish(pairs: 2000, withBom: true);

        Assert.True((await Installer().InstallAsync(ct: CancellationToken.None)).Installed);
    }

    private CoReadCache Cache() =>
        new(new CoReadOptions(_graphPath, _dir), NullLogger<CoReadCache>.Instance);

    private CoReadInstaller Installer(CoReadCache? cache = null) =>
        new(new StubHttpClientFactory(_handler),
            new CoReadOptions(_graphPath, _dir),
            cache ?? Cache(),
            _settings,
            NullLogger<CoReadInstaller>.Instance);

    private void Publish(
        int pairs,
        long? advertisedPairs = null,
        int schemaVersion = 1,
        int nullStrengthRows = 0,
        int zeroStrengthRows = 0,
        int selfPairRows = 0,
        string? sha256Override = null,
        bool withBom = false)
    {
        var sourcePath = Path.Combine(_dir, $"artifact-{Guid.NewGuid():N}.db");
        using (var conn = new SqliteConnection($"Data Source={sourcePath};Pooling=False"))
        {
            conn.Open();
            using var create = conn.CreateCommand();
            create.CommandText = PairSchema + """
                CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                """;
            create.ExecuteNonQuery();

            InsertPairs(conn, pairs, nullStrengthRows, zeroStrengthRows, selfPairRows);

            using var meta = conn.CreateCommand();
            meta.CommandText =
                "INSERT INTO meta (key, value) VALUES ('schemaVersion', $s), ('generatedAt', $at)";
            meta.Parameters.AddWithValue("$s", schemaVersion.ToString());
            meta.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
            meta.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        PublishBytes(
            File.ReadAllBytes(sourcePath), pairs + selfPairRows, advertisedPairs, schemaVersion,
            sha256Override, withBom);
    }

    /// <summary>The fetcher's working database, which holds one row per user per series read.</summary>
    private void PublishWorkingDatabase(bool withPairTable = false, int pairs = 0)
    {
        var sourcePath = Path.Combine(_dir, $"working-{Guid.NewGuid():N}.db");
        using (var conn = new SqliteConnection($"Data Source={sourcePath};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE user_entry (
                    user_id INTEGER NOT NULL, media_id INTEGER NOT NULL,
                    score INTEGER, status TEXT);
                CREATE TABLE user_state (user_id INTEGER PRIMARY KEY, status TEXT, entries INTEGER);
                CREATE TABLE pending_user (user_id INTEGER PRIMARY KEY);
                CREATE TABLE cooccurrence (
                    a_id INTEGER, b_id INTEGER, support INTEGER, strength REAL);
                CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO user_entry VALUES (7, 30002, 90, 'COMPLETED');
                INSERT INTO meta (key, value) VALUES ('schemaVersion', '1');
                """ + (withPairTable ? PairSchema : string.Empty);
            cmd.ExecuteNonQuery();

            if (withPairTable && pairs > 0)
            {
                InsertPairs(conn, pairs, 0, 0, 0);
            }
        }

        SqliteConnection.ClearAllPools();
        PublishBytes(File.ReadAllBytes(sourcePath), Math.Max(pairs, 50_000), null, 1, null, withBom: false);
    }

    private const string PairSchema = """
        CREATE TABLE pair (
            a_id INTEGER NOT NULL, b_id INTEGER NOT NULL,
            support INTEGER NOT NULL DEFAULT 3, strength REAL,
            PRIMARY KEY (a_id, b_id)) WITHOUT ROWID;
        """;

    private static void InsertPairs(
        SqliteConnection conn, int pairs, int nullStrengthRows, int zeroStrengthRows, int selfPairRows)
    {
        using var tx = conn.BeginTransaction();
        using var insert = conn.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = "INSERT INTO pair (a_id, b_id, strength) VALUES ($a, $b, $s)";
        var a = insert.Parameters.Add("$a", SqliteType.Integer);
        var b = insert.Parameters.Add("$b", SqliteType.Integer);
        var st = insert.Parameters.Add("$s", SqliteType.Real);

        for (var i = 0; i < pairs; i++)
        {
            a.Value = i + 1;
            b.Value = 1_000_000 + i;

            // The column is declared NOT NULL by the exporter's schema, and a file that got its
            // strengths wrong would not have been written by the exporter — so the fixture drops
            // that constraint for these rows rather than pretending a valid export can hold them.
            if (i < nullStrengthRows)
            {
                st.Value = DBNull.Value;
            }
            else if (i < nullStrengthRows + zeroStrengthRows)
            {
                st.Value = 0.0;
            }
            else
            {
                st.Value = 0.01 + (i % 50 * 0.001);
            }

            insert.ExecuteNonQuery();
        }

        for (var i = 0; i < selfPairRows; i++)
        {
            a.Value = 5_000_000 + i;
            b.Value = 5_000_000 + i;
            st.Value = 0.5;
            insert.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private void PublishBytes(
        byte[] raw, long pairCount, long? advertisedPairs, int schemaVersion, string? sha256Override,
        bool withBom)
    {
        using var compressor = new Compressor(3);
        var compressed = compressor.Wrap(raw).ToArray();
        _handler.Artifact = compressed;

        var manifest = JsonSerializer.Serialize(new
        {
            schemaVersion,
            pairCount = advertisedPairs ?? pairCount,
            seriesCount = pairCount * 2,
            userCount = 8828,
            generatedAt = DateTime.UtcNow,
            fileName = "coread-edges.db.zst",
            sizeBytes = compressed.Length,
            sha256 = sha256Override
                ?? Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(compressed)),
            url = "https://example.test/coread-edges.db.zst",
        });

        _handler.Manifest = withBom
            ? [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(manifest)]
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
