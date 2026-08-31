using System.Net;
using System.Text;
using System.Text.Json;
using Maki.Core.Configuration;
using Maki.Metadata.RecoGraph;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZstdSharp;

namespace Maki.Metadata.Tests;

/// <summary>
/// The download path for <c>reco-edges.db</c>. The guards worth testing are the ones whose failure
/// is <em>quiet</em>: a wrong-shaped artifact does not crash anything, it loads as an empty or
/// nonsense graph and the channel just stops contributing, which looks identical to the normal
/// state of an install that has no artifact at all.
/// </summary>
public class RecoGraphInstallerTests : IDisposable
{
    private readonly string _dir;
    private readonly string _graphPath;
    private readonly FakeAppSettings _settings = new();
    private readonly StubHandler _handler = new();

    public RecoGraphInstallerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "maki-recograph-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _graphPath = Path.Combine(_dir, "reco-edges.db");
    }

    [Fact]
    public async Task Installs_AValidArtifact()
    {
        Publish(pairs: 2000);

        var result = await Installer().InstallAsync(ct: CancellationToken.None);

        Assert.True(result.Installed, result.Reason);
        Assert.Equal(2000, result.PairCount);
        Assert.True(File.Exists(_graphPath));
        // The install marker is what later runs compare against for freshness.
        Assert.NotNull(_settings.Values.GetValueOrDefault(SettingKeys.RecommendationsCoGraphGeneratedAt));
    }

    [Fact]
    public async Task InstalledGraph_IsImmediatelyReadable()
    {
        // The swap has to invalidate the cache too, or the channel keeps answering from whatever
        // was loaded before the download and the install looks like it did nothing.
        Publish(pairs: 2000);
        var cache = Cache();

        Assert.Null(await cache.GetAsync());
        Assert.True((await Installer(cache).InstallAsync(ct: CancellationToken.None)).Installed);

        var graph = await cache.GetAsync();
        Assert.NotNull(graph);
        Assert.Equal(2000 * 2, graph.EdgeCount);
    }

    [Fact]
    public async Task Refuses_TheFetchersWorkingDatabase()
    {
        // reco-graph.db and reco-edges.db are both SQLite files with near-identical names sitting
        // in the same folder. Publishing the wrong one would install a file with no `pair` table,
        // which loads as no graph at all rather than as an error.
        PublishWorkingDatabase();

        var result = await Installer().InstallAsync(ct: CancellationToken.None);

        Assert.False(result.Installed);
        Assert.Contains("no 'pair' table", result.Reason);
        Assert.False(File.Exists(_graphPath));
    }

    [Fact]
    public async Task Refuses_AnArtifactWithNegativeVotes()
    {
        // log1p of a negative is NaN, and one NaN propagates through every score it touches.
        Publish(pairs: 2000, negativeVoteRows: 3);

        var result = await Installer().InstallAsync(ct: CancellationToken.None);

        Assert.False(result.Installed);
        Assert.Contains("negative vote", result.Reason);
        Assert.False(File.Exists(_graphPath));
    }

    [Fact]
    public async Task Refuses_AnArtifactWithSelfPairs()
    {
        Publish(pairs: 2000, selfPairRows: 2);

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
        Publish(pairs: 2000, schemaVersion: RecoGraphInstaller.SupportedSchemaVersion + 1);

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

        var forced = await installer.InstallAsync(force: true, ct: CancellationToken.None);

        Assert.True(forced.Installed, forced.Reason);
    }

    [Fact]
    public async Task Force_StillRespectsValidation()
    {
        // "Download now" must not be a way to install a file this build cannot use.
        Publish(pairs: 2000, selfPairRows: 1);

        var result = await Installer().InstallAsync(force: true, ct: CancellationToken.None);

        Assert.False(result.Installed);
        Assert.Contains("self-pair", result.Reason);
    }

    [Fact]
    public async Task Skips_WhenTheChannelIsTurnedOff()
    {
        // No point downloading a file nothing may read. Same switch the recommender checks.
        Publish(pairs: 2000);
        _settings.Values[SettingKeys.RecommendationsCoGraph] = "false";

        var result = await Installer().InstallAsync(force: true, ct: CancellationToken.None);

        Assert.False(result.Installed);
        Assert.Contains("turned off", result.Reason);
        Assert.False(File.Exists(_graphPath));
    }

    [Fact]
    public async Task Skips_WhenNothingIsPublished()
    {
        // Today's normal state for every install. Has to be quiet, and has to leave no file.
        _handler.ManifestStatus = HttpStatusCode.NotFound;

        var result = await Installer().InstallAsync(ct: CancellationToken.None);

        Assert.False(result.Installed);
        Assert.Contains("manifest", result.Reason);
        Assert.False(File.Exists(_graphPath));
    }

    [Fact]
    public async Task Installs_OverAHandPlacedFileWithNoMarker()
    {
        // A file the user dropped in themselves carries no install marker, so freshness cannot be
        // judged against it. The published artifact is the known quantity and wins.
        await File.WriteAllTextAsync(_graphPath, "hand-placed, provenance unknown");
        Publish(pairs: 2000);

        var result = await Installer().InstallAsync(ct: CancellationToken.None);

        Assert.True(result.Installed, result.Reason);
    }

    [Fact]
    public async Task Tolerates_AManifestWithAByteOrderMark()
    {
        // The publish script runs on Windows PowerShell, which is fond of BOMs.
        Publish(pairs: 2000, withBom: true);

        var result = await Installer().InstallAsync(ct: CancellationToken.None);

        Assert.True(result.Installed, result.Reason);
    }

    private RecoGraphCache Cache() =>
        new(new RecoGraphOptions(_graphPath, _dir), NullLogger<RecoGraphCache>.Instance);

    private RecoGraphInstaller Installer(RecoGraphCache? cache = null) =>
        new(new StubHttpClientFactory(_handler),
            new RecoGraphOptions(_graphPath, _dir),
            cache ?? Cache(),
            _settings,
            NullLogger<RecoGraphInstaller>.Instance);

    /// <summary>Builds a compressed artifact + manifest and puts them behind the stub HTTP handler.</summary>
    private void Publish(
        int pairs,
        long? advertisedPairs = null,
        int schemaVersion = 1,
        int negativeVoteRows = 0,
        int selfPairRows = 0,
        string? sha256Override = null,
        bool withBom = false)
    {
        var sourcePath = Path.Combine(_dir, $"artifact-{Guid.NewGuid():N}.db");
        using (var conn = new SqliteConnection($"Data Source={sourcePath};Pooling=False"))
        {
            conn.Open();
            using var create = conn.CreateCommand();
            create.CommandText = """
                CREATE TABLE pair (
                    a_id INTEGER NOT NULL, b_id INTEGER NOT NULL,
                    anilist_votes INTEGER NOT NULL DEFAULT 0, mal_votes INTEGER NOT NULL DEFAULT 0,
                    directions INTEGER NOT NULL DEFAULT 1, PRIMARY KEY (a_id, b_id)) WITHOUT ROWID;
                CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                """;
            create.ExecuteNonQuery();

            using var tx = conn.BeginTransaction();
            using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText =
                "INSERT INTO pair (a_id, b_id, anilist_votes) VALUES ($a, $b, $v)";
            var a = insert.Parameters.Add("$a", SqliteType.Integer);
            var b = insert.Parameters.Add("$b", SqliteType.Integer);
            var v = insert.Parameters.Add("$v", SqliteType.Integer);

            for (var i = 0; i < pairs; i++)
            {
                // Distinct ids per row, so the pair count is exactly `pairs` and the primary key
                // never collides.
                a.Value = i + 1;
                b.Value = 1_000_000 + i;
                v.Value = i < negativeVoteRows ? -1 : (i % 50) + 1;
                insert.ExecuteNonQuery();
            }

            for (var i = 0; i < selfPairRows; i++)
            {
                a.Value = 5_000_000 + i;
                b.Value = 5_000_000 + i;
                v.Value = 3;
                insert.ExecuteNonQuery();
            }

            using var meta = conn.CreateCommand();
            meta.Transaction = tx;
            meta.CommandText =
                "INSERT INTO meta (key, value) VALUES ('schemaVersion', $s), ('generatedAt', $at)";
            meta.Parameters.AddWithValue("$s", schemaVersion.ToString());
            meta.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
            meta.ExecuteNonQuery();
            tx.Commit();
        }

        SqliteConnection.ClearAllPools();
        PublishBytes(File.ReadAllBytes(sourcePath), pairs + selfPairRows, advertisedPairs, schemaVersion, sha256Override, withBom);
    }

    /// <summary>An artifact with the fetcher's tables instead of the export's.</summary>
    private void PublishWorkingDatabase()
    {
        var sourcePath = Path.Combine(_dir, $"working-{Guid.NewGuid():N}.db");
        using (var conn = new SqliteConnection($"Data Source={sourcePath};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE edge (
                    provider TEXT NOT NULL, from_id INTEGER NOT NULL,
                    to_id INTEGER NOT NULL, votes INTEGER NOT NULL);
                CREATE TABLE fetch_state (provider TEXT, remote_id INTEGER, status TEXT);
                """;
            cmd.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        PublishBytes(File.ReadAllBytes(sourcePath), 50_000, null, 1, null, withBom: false);
    }

    private void PublishBytes(
        byte[] raw, long pairCount, long? advertisedPairs, int schemaVersion, string? sha256Override, bool withBom)
    {
        using var compressor = new Compressor(3);
        var compressed = compressor.Wrap(raw).ToArray();
        _handler.Artifact = compressed;

        var manifest = JsonSerializer.Serialize(new
        {
            schemaVersion,
            pairCount = advertisedPairs ?? pairCount,
            seriesCount = pairCount * 2,
            providers = "anilist",
            generatedAt = DateTime.UtcNow,
            fileName = "reco-edges.db.zst",
            sizeBytes = compressed.Length,
            sha256 = sha256Override
                ?? Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(compressed)),
            url = "https://example.test/reco-edges.db.zst",
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
