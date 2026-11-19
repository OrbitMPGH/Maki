using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maki.Core.Configuration;
using Maki.Metadata.MangaBaka;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ZstdSharp;

namespace Maki.Metadata.PageShare;

/// <summary>
/// What the published manifest says about the page-share table. Field names match the JSON written
/// by <c>distribution/graph-artifact.cs pageshare</c>.
/// </summary>
public record PageShareManifest
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; }

    [JsonPropertyName("rowCount")] public long RowCount { get; init; }

    [JsonPropertyName("readers")] public long Readers { get; init; }

    /// <summary>
    /// Which reader folds the pages came from. Anything but <c>all</c> is an evaluation build, made
    /// with fold-limited artifacts, and is refused.
    /// </summary>
    [JsonPropertyName("trainingFold")] public string? TrainingFold { get; init; }

    [JsonPropertyName("generatedAt")] public DateTime? GeneratedAt { get; init; }

    [JsonPropertyName("sizeBytes")] public long SizeBytes { get; init; }

    [JsonPropertyName("sha256")] public string? Sha256 { get; init; }

    [JsonPropertyName("url")] public string? Url { get; init; }
}

/// <summary>
/// Outcome of one install attempt. <see cref="Reason"/> is a server message catalogue key, except
/// <c>Install failed: {ex.Message}</c>, as on the other installers.
/// </summary>
public record PageShareResult(bool Installed, string Reason, long? RowCount = null, object? ReasonArgs = null);

/// <summary>
/// Downloads and installs the page-share table published alongside Maki. Shaped like
/// <see cref="Ease.EaseInstaller"/>, with the same refusals: a file carrying per-user tables, and a
/// fold-limited evaluation build. The table is built from the engine's own pages on AniList
/// reading lists, which do not ship, so a download is the only way an install gets it.
/// </summary>
public class PageShareInstaller(
    IHttpClientFactory httpClientFactory,
    PageShareOptions options,
    PageShareCache cache,
    IAppSettings settings,
    ILogger<PageShareInstaller> logger)
{
    public const string HttpClientName = "page-share";

    public const int SupportedSchemaVersion = 1;

    /// <summary>A moving tag, so the manifest URL stays the same across republishes.</summary>
    public const string ReleaseTag = "page-share-latest";

    public string DefaultManifestUrl =>
        $"https://github.com/OrbitMPGH/Maki/releases/download/{ReleaseTag}/manifest.json";

    /// <summary>Sanity floor: a real table holds several thousand series.</summary>
    internal const long MinRows = 1_000;

    private readonly SemaphoreSlim _installGate = new(1, 1);

    /// <summary>Tied to the penalty's kill switch, default on.</summary>
    public async Task<bool> IsEnabledAsync(CancellationToken ct = default) =>
        !string.Equals(await settings.GetAsync(SettingKeys.RecommendationsPageShare, ct), "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Installs the published table when it is compatible and newer than what is here.
    /// <paramref name="force"/> skips only the freshness check, never the safety ones.
    /// </summary>
    public async Task<PageShareResult> InstallAsync(bool force = false, CancellationToken ct = default)
    {
        if (!await IsEnabledAsync(ct))
        {
            return new PageShareResult(false, "install.pageShare.disabled");
        }

        var manifestUrl = await settings.GetAsync(SettingKeys.RecommendationsPageShareUrl, ct);
        manifestUrl = string.IsNullOrWhiteSpace(manifestUrl) ? DefaultManifestUrl : manifestUrl.Trim();

        var client = httpClientFactory.CreateClient(HttpClientName);
        PageShareManifest? manifest;
        try
        {
            var json = await client.GetStringAsync(manifestUrl, ct);
            manifest = JsonSerializer.Deserialize<PageShareManifest>(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogDebug(ex, "Page-share table manifest unavailable at {Url}", manifestUrl);
            return new PageShareResult(false, "install.pageShare.manifestUnavailable");
        }

        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Url))
        {
            return new PageShareResult(false, "install.pageShare.manifestMalformed");
        }

        if (manifest.SchemaVersion > SupportedSchemaVersion)
        {
            logger.LogInformation(
                "Ignoring the published page-share table: schema {Theirs}, this build reads {Ours}",
                manifest.SchemaVersion, SupportedSchemaVersion);
            return new PageShareResult(false, "install.pageShare.schemaNewer");
        }

        if (manifest.RowCount < MinRows)
        {
            return new PageShareResult(false, "install.pageShare.truncated");
        }

        if (IsFoldLimited(manifest.TrainingFold))
        {
            logger.LogWarning(
                "Refusing the published page-share table: built on folds '{Folds}', not the whole population",
                manifest.TrainingFold);
            return new PageShareResult(false, "install.pageShare.evaluationBuild");
        }

        if (!force && !await IsNewerThanLocalAsync(manifest, ct))
        {
            return new PageShareResult(false, "install.pageShare.current");
        }

        var stagingDir = options.StagingDirectory ?? Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath))!;
        Directory.CreateDirectory(stagingDir);
        var staging = Path.Combine(stagingDir, "page-share.db.partial");

        if (!_installGate.Wait(0))
        {
            return new PageShareResult(false, "install.alreadyRunning");
        }

        try
        {
            await DownloadAndDecompressAsync(client, manifest, staging, ct);
            var rows = ValidateStaged(staging, manifest.RowCount);

            await cache.SwapDatabaseAsync(staging, ct);
            await settings.SetAsync(
                SettingKeys.RecommendationsPageShareGeneratedAt,
                (manifest.GeneratedAt ?? DateTime.UtcNow).ToString("O", CultureInfo.InvariantCulture),
                ct);

            logger.LogInformation("Installed the page-share table ({Rows} series)", rows);
            return new PageShareResult(true, "install.pageShare.installed", rows, new { rows });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Page-share table install failed");
            return new PageShareResult(false, $"Install failed: {ex.Message}");
        }
        finally
        {
            TryDelete(staging);
            _installGate.Release();
        }
    }

    private async Task<bool> IsNewerThanLocalAsync(PageShareManifest manifest, CancellationToken ct)
    {
        if (!File.Exists(options.DatabasePath))
        {
            return true;
        }

        var installedAt = await settings.GetAsync(SettingKeys.RecommendationsPageShareGeneratedAt, ct);
        if (!DateTime.TryParse(installedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var installed))
        {
            return true;
        }

        return manifest.GeneratedAt is not { } published || published > installed;
    }

    private async Task DownloadAndDecompressAsync(
        HttpClient client, PageShareManifest manifest, string staging, CancellationToken ct)
    {
        using var response = await client.GetAsync(manifest.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var network = new StallTimeoutStream(await response.Content.ReadAsStreamAsync(ct)))
        await using (var hashing = new HashingReadStream(network, sha256))
        await using (var decompressor = new DecompressionStream(hashing))
        await using (var output = File.Create(staging))
        {
            await decompressor.CopyToAsync(output, ct);
        }

        if (string.IsNullOrWhiteSpace(manifest.Sha256))
        {
            return;
        }

        var actual = Convert.ToHexStringLower(sha256.GetHashAndReset());
        if (!string.Equals(actual, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"checksum mismatch (expected {manifest.Sha256}, got {actual})");
        }
    }

    /// <summary>
    /// Opens the staged file and proves it is a usable, publishable table before it replaces the
    /// live one. Per-user tables first, before anything else.
    /// </summary>
    internal static long ValidateStaged(string staging, long advertisedRows = 0)
    {
        using var conn = new SqliteConnection($"Data Source={staging};Mode=ReadOnly;Pooling=False");
        conn.Open();

        ArtifactChecks.RefusePerUserTables(conn, "page-share builder's");
        ArtifactChecks.RequireIntegrity(conn, "table");

        using (var shape = conn.CreateCommand())
        {
            shape.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'share'";
            if (shape.ExecuteScalar() is not long found || found == 0)
            {
                throw new InvalidOperationException("downloaded file has no 'share' table");
            }
        }

        using (var fold = conn.CreateCommand())
        {
            fold.CommandText = "SELECT value FROM meta WHERE key = 'trainingFold'";
            string? trained;
            try
            {
                trained = fold.ExecuteScalar()?.ToString();
            }
            catch (SqliteException)
            {
                throw new InvalidOperationException("downloaded file has no meta table");
            }

            if (string.IsNullOrWhiteSpace(trained))
            {
                throw new InvalidOperationException("downloaded table does not say which readers its pages came from");
            }

            if (IsFoldLimited(trained))
            {
                throw new InvalidOperationException(
                    $"downloaded table is a fold-limited evaluation build (folds '{trained}')");
            }
        }

        // `share IS NULL OR NOT (share BETWEEN 0 AND 1)`: SQLite stores a NaN as NULL, and a plain
        // range test against NULL would let exactly that row through.
        using var stats = conn.CreateCommand();
        stats.CommandText = """
            SELECT COUNT(*),
                   COALESCE(SUM(CASE WHEN share IS NULL OR NOT (share BETWEEN 0 AND 1) THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN typeof(series_id) <> 'integer' THEN 1 ELSE 0 END), 0)
            FROM share
            """;
        using var reader = stats.ExecuteReader();
        reader.Read();
        var rows = reader.GetInt64(0);
        var bad = reader.GetInt64(1);
        var badIds = reader.GetInt64(2);

        if (rows < MinRows)
        {
            throw new InvalidOperationException($"downloaded table holds only {rows} series");
        }

        if (bad > 0)
        {
            throw new InvalidOperationException($"downloaded table has {bad} missing or out-of-range shares");
        }

        if (badIds > 0)
        {
            throw new InvalidOperationException($"downloaded table has {badIds} rows without an integer series id");
        }

        if (advertisedRows > 0 && rows < advertisedRows * 0.95)
        {
            throw new InvalidOperationException(
                $"downloaded table holds {rows} series, well short of the {advertisedRows} advertised");
        }

        return rows;
    }

    private static bool IsFoldLimited(string? trainingFold) =>
        trainingFold is { Length: > 0 } value && !value.Equals("all", StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }
}
