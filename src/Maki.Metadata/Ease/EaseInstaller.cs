using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maki.Core.Configuration;
using Maki.Metadata.MangaBaka;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ZstdSharp;

namespace Maki.Metadata.Ease;

/// <summary>
/// What the published manifest says about the EASE table. Field names match the JSON written by
/// <c>distribution/graph-artifact.cs ease</c>.
/// </summary>
public record EaseManifest
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; }

    [JsonPropertyName("weightCount")] public long WeightCount { get; init; }

    [JsonPropertyName("itemCount")] public long ItemCount { get; init; }

    [JsonPropertyName("trainedReaders")] public long TrainedReaders { get; init; }

    /// <summary>
    /// Which reader folds the table learned from. Anything but <c>all</c> is an evaluation build
    /// (<c>build-ease-table.py --fold-out</c>) and is refused.
    /// </summary>
    [JsonPropertyName("trainingFold")] public string? TrainingFold { get; init; }

    [JsonPropertyName("generatedAt")] public DateTime? GeneratedAt { get; init; }

    [JsonPropertyName("sizeBytes")] public long SizeBytes { get; init; }

    [JsonPropertyName("sha256")] public string? Sha256 { get; init; }

    [JsonPropertyName("url")] public string? Url { get; init; }
}

/// <summary>
/// Outcome of one install attempt. <see cref="Reason"/> is a server message catalogue key the
/// caller in <c>Maki.Api</c> renders, except <c>Install failed: {ex.Message}</c>, as on the other
/// installers.
/// </summary>
public record EaseResult(bool Installed, string Reason, long? WeightCount = null, object? ReasonArgs = null);

/// <summary>
/// Downloads and installs the EASE weight table published alongside Maki. Shaped like
/// <see cref="CoRead.CoReadInstaller"/>, with the fold refusal from
/// <see cref="Taste.TasteVectorInstaller"/>: the table is fitted on the co-read fetcher's working
/// database, which holds per-user reading rows and does not ship, so a download is the only way an
/// install gets the channel, a file carrying those rows is refused outright, and a fold-limited
/// evaluation build (valid, and silently missing a quarter of the readers) is refused too.
/// </summary>
public class EaseInstaller(
    IHttpClientFactory httpClientFactory,
    EaseOptions options,
    EaseCache cache,
    IAppSettings settings,
    ILogger<EaseInstaller> logger)
{
    public const string HttpClientName = "ease-table";

    /// <summary>The schema this build reads. A newer artifact is refused rather than half-read.</summary>
    public const int SupportedSchemaVersion = 1;

    /// <summary>A moving tag, so the manifest URL stays the same across republishes.</summary>
    public const string ReleaseTag = "ease-table-latest";

    public string DefaultManifestUrl =>
        $"https://github.com/OrbitMPGH/Maki/releases/download/{ReleaseTag}/manifest.json";

    /// <summary>
    /// Sanity floor: a real table carries about 2M weights (100 per item over 20,000 items), so
    /// anything this small is a truncated or half-written file.
    /// </summary>
    internal const long MinWeights = 100_000;

    private readonly SemaphoreSlim _installGate = new(1, 1);

    /// <summary>Tied to the channel's kill switch, default on, like the co-read installer.</summary>
    public async Task<bool> IsEnabledAsync(CancellationToken ct = default) =>
        !string.Equals(await settings.GetAsync(SettingKeys.RecommendationsEase, ct), "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Installs the published table when it is compatible and newer than what is here.
    /// <paramref name="force"/> skips only the freshness check, never the safety ones.
    /// </summary>
    public async Task<EaseResult> InstallAsync(bool force = false, CancellationToken ct = default)
    {
        if (!await IsEnabledAsync(ct))
        {
            return new EaseResult(false, "install.ease.disabled");
        }

        var manifestUrl = await settings.GetAsync(SettingKeys.RecommendationsEaseUrl, ct);
        manifestUrl = string.IsNullOrWhiteSpace(manifestUrl) ? DefaultManifestUrl : manifestUrl.Trim();

        var client = httpClientFactory.CreateClient(HttpClientName);
        EaseManifest? manifest;
        try
        {
            // From a string, not the bytes: a manifest written on Windows can carry a BOM.
            var json = await client.GetStringAsync(manifestUrl, ct);
            manifest = JsonSerializer.Deserialize<EaseManifest>(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Debug: until an artifact is published this is every install's normal state.
            logger.LogDebug(ex, "Also-finished table manifest unavailable at {Url}", manifestUrl);
            return new EaseResult(false, "install.ease.manifestUnavailable");
        }

        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Url))
        {
            return new EaseResult(false, "install.ease.manifestMalformed");
        }

        if (manifest.SchemaVersion > SupportedSchemaVersion)
        {
            logger.LogInformation(
                "Ignoring the published also-finished table: schema {Theirs}, this build reads {Ours}",
                manifest.SchemaVersion, SupportedSchemaVersion);
            return new EaseResult(false, "install.ease.schemaNewer");
        }

        if (manifest.WeightCount < MinWeights)
        {
            return new EaseResult(false, "install.ease.truncated");
        }

        // From the manifest too, so an evaluation build is not even downloaded. ValidateStaged is
        // the check that protects an install, since a manifest can say anything.
        if (IsFoldLimited(manifest.TrainingFold))
        {
            logger.LogWarning(
                "Refusing the published also-finished table: trained on folds '{Folds}', not the whole population",
                manifest.TrainingFold);
            return new EaseResult(false, "install.ease.evaluationBuild");
        }

        if (!force && !await IsNewerThanLocalAsync(manifest, ct))
        {
            return new EaseResult(false, "install.ease.current");
        }

        var stagingDir = options.StagingDirectory ?? Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath))!;
        Directory.CreateDirectory(stagingDir);
        var staging = Path.Combine(stagingDir, "ease-table.db.partial");

        if (!_installGate.Wait(0))
        {
            return new EaseResult(false, "install.alreadyRunning");
        }

        try
        {
            await DownloadAndDecompressAsync(client, manifest, staging, ct);
            var weights = ValidateStaged(staging, manifest.WeightCount);

            await cache.SwapDatabaseAsync(staging, ct);
            await settings.SetAsync(
                SettingKeys.RecommendationsEaseGeneratedAt,
                (manifest.GeneratedAt ?? DateTime.UtcNow).ToString("O", CultureInfo.InvariantCulture),
                ct);

            logger.LogInformation("Installed the also-finished table ({Weights} weights)", weights);
            return new EaseResult(true, "install.ease.installed", weights, new { weights });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Also-finished table install failed");
            return new EaseResult(false, $"Install failed: {ex.Message}");
        }
        finally
        {
            TryDelete(staging);
            _installGate.Release();
        }
    }

    private async Task<bool> IsNewerThanLocalAsync(EaseManifest manifest, CancellationToken ct)
    {
        if (!File.Exists(options.DatabasePath))
        {
            return true;
        }

        var installedAt = await settings.GetAsync(SettingKeys.RecommendationsEaseGeneratedAt, ct);
        if (!DateTime.TryParse(installedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var installed))
        {
            return true;
        }

        return manifest.GeneratedAt is not { } published || published > installed;
    }

    private async Task DownloadAndDecompressAsync(
        HttpClient client, EaseManifest manifest, string staging, CancellationToken ct)
    {
        logger.LogInformation(
            "Downloading the also-finished table ({Size:N0} MB)…", manifest.SizeBytes / 1_000_000);

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
    internal static long ValidateStaged(string staging, long advertisedWeights = 0)
    {
        using var conn = new SqliteConnection($"Data Source={staging};Mode=ReadOnly;Pooling=False");
        conn.Open();

        ArtifactChecks.RefusePerUserTables(conn, "builder's");
        ArtifactChecks.RequireIntegrity(conn, "table");

        using (var shape = conn.CreateCommand())
        {
            shape.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'weight'";
            if (shape.ExecuteScalar() is not long found || found == 0)
            {
                throw new InvalidOperationException("downloaded file has no 'weight' table");
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

            // The builder always writes the key, so a file without it did not come from the builder
            // and nothing says which readers it saw.
            if (string.IsNullOrWhiteSpace(trained))
            {
                throw new InvalidOperationException("downloaded table does not say which readers it was trained on");
            }

            if (IsFoldLimited(trained))
            {
                throw new InvalidOperationException(
                    $"downloaded table is a fold-limited evaluation build (trained on folds '{trained}')");
            }
        }

        // `weight IS NULL OR weight = 0`: SQLite has no NaN and stores one as NULL, and a
        // comparison against NULL would let exactly that row through. The ABS bound catches an
        // infinity and any double the float read in EaseCache would turn into one; EaseTable.Build
        // drops all of these, so a table made of them would install and serve nothing.
        using var stats = conn.CreateCommand();
        stats.CommandText = """
            SELECT COUNT(*),
                   COALESCE(SUM(CASE WHEN seed_id = item_id THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN weight IS NULL OR weight = 0 OR ABS(weight) > 3.4028234663852886e38 THEN 1 ELSE 0 END), 0)
            FROM weight
            """;
        stats.CommandTimeout = 600;
        using var reader = stats.ExecuteReader();
        reader.Read();
        var weights = reader.GetInt64(0);
        var self = reader.GetInt64(1);
        var empty = reader.GetInt64(2);

        if (weights < MinWeights)
        {
            throw new InvalidOperationException($"downloaded table holds only {weights} weights");
        }

        if (self > 0)
        {
            throw new InvalidOperationException($"downloaded table has {self} self-weights");
        }

        if (empty > 0)
        {
            throw new InvalidOperationException($"downloaded table has {empty} missing, zero or non-finite weights");
        }

        if (advertisedWeights > 0 && weights < advertisedWeights * 0.95)
        {
            throw new InvalidOperationException(
                $"downloaded table holds {weights} weights, well short of the {advertisedWeights} advertised");
        }

        return weights;
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
