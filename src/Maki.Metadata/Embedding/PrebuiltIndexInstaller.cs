using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maki.Core.Configuration;
using Maki.Metadata.MangaBaka;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ZstdSharp;

namespace Maki.Metadata.Embedding;

/// <summary>
/// What the published manifest says about an artifact. Field names match the JSON written by
/// <c>distribution/publish-embeddings.ps1</c>.
/// </summary>
public record PrebuiltIndexManifest
{
    [JsonPropertyName("modelVersion")] public string? ModelVersion { get; init; }

    [JsonPropertyName("dimensions")] public int Dimensions { get; init; }

    [JsonPropertyName("rowCount")] public long RowCount { get; init; }

    [JsonPropertyName("generatedAt")] public DateTime? GeneratedAt { get; init; }

    [JsonPropertyName("sizeBytes")] public long SizeBytes { get; init; }

    [JsonPropertyName("sha256")] public string? Sha256 { get; init; }

    [JsonPropertyName("url")] public string? Url { get; init; }
}

/// <summary>Outcome of one install attempt, for logs and the settings UI.</summary>
public record PrebuiltIndexResult(bool Installed, string Reason, long? RowCount = null);

/// <summary>
/// Downloads and installs the prebuilt embedding index published alongside Maki, so a fresh
/// install doesn't spend ~an hour of CPU deriving vectors that are identical on every machine.
///
/// Everything here is derived public data, so overwriting the local index loses nothing the user
/// authored — a bad swap costs CPU time, not content. That is what makes the blunt "replace the
/// file" approach acceptable, and it's why the guards are all about *compatibility* rather than
/// data safety: a file of the wrong width or model would leave search silently empty (every row
/// filtered out by <see cref="VectorIndexCache"/>) rather than loudly broken.
/// </summary>
public class PrebuiltIndexInstaller(
    IHttpClientFactory httpClientFactory,
    EmbeddingOptions options,
    EmbeddingStore store,
    VectorIndexCache cache,
    EmbeddingIndexStatus indexStatus,
    IAppSettings settings,
    ILogger<PrebuiltIndexInstaller> logger)
{
    public const string HttpClientName = "prebuilt-index";

    /// <summary>
    /// Where the artifact for the configured model is published, unless overridden in settings.
    /// Each model has its own release tag, so a base install and a large install fetch different
    /// files — and the compatibility gate below rejects the wrong one anyway.
    /// </summary>
    public string DefaultManifestUrl =>
        $"https://github.com/OrbitMPGH/Maki/releases/download/{options.Model.PrebuiltTag}/manifest.json";

    /// <summary>Sanity floor: a "full" catalogue artifact that's tiny is a mispublish.</summary>
    private const long MinRows = 1000;

    public async Task<bool> IsEnabledAsync(CancellationToken ct = default) =>
        !string.Equals(
            await settings.GetAsync(SettingKeys.RecommendationsPrebuiltEnabled, ct),
            "false",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Installs the published index when it is compatible with this build and newer than what's
    /// already here. <paramref name="force"/> skips only the freshness check (for a manual
    /// "download now"), never the compatibility ones.
    /// </summary>
    public async Task<PrebuiltIndexResult> InstallAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && !await IsEnabledAsync(ct))
        {
            return new PrebuiltIndexResult(false, "Prebuilt index downloads are disabled.");
        }

        // Never swap the file out from under a running pass: the indexer holds its own
        // connections and would write its results back over whatever we installed.
        if (indexStatus.Running)
        {
            return new PrebuiltIndexResult(false, "An indexing pass is running; will retry later.");
        }

        var manifestUrl = await settings.GetAsync(SettingKeys.RecommendationsPrebuiltUrl, ct);
        manifestUrl = string.IsNullOrWhiteSpace(manifestUrl) ? DefaultManifestUrl : manifestUrl.Trim();

        var client = httpClientFactory.CreateClient(HttpClientName);
        PrebuiltIndexManifest? manifest;
        try
        {
            // Deserialized from a string rather than the response bytes: a manifest written by a
            // Windows tool can carry a UTF-8 BOM, which byte-level JSON parsing rejects outright.
            var json = await client.GetStringAsync(manifestUrl, ct);
            manifest = JsonSerializer.Deserialize<PrebuiltIndexManifest>(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogDebug(ex, "Prebuilt index manifest unavailable at {Url}", manifestUrl);
            return new PrebuiltIndexResult(false, "Could not read the prebuilt index manifest.");
        }

        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Url))
        {
            return new PrebuiltIndexResult(false, "The prebuilt index manifest is malformed.");
        }

        // Compatibility: a mismatch here is the failure that hides. Vectors of the wrong width
        // are dropped row-by-row at load, so search would just quietly return nothing.
        if (!string.Equals(manifest.ModelVersion, options.ModelVersion, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation(
                "Ignoring the prebuilt index: it was built with model {Theirs}, this install uses {Ours}",
                manifest.ModelVersion, options.ModelVersion);
            return new PrebuiltIndexResult(false, "The published index was built for a different embedding model.");
        }

        if (manifest.Dimensions != options.Dimensions)
        {
            return new PrebuiltIndexResult(
                false, $"The published index is {manifest.Dimensions}-dimensional; this build needs {options.Dimensions}.");
        }

        if (manifest.RowCount < MinRows)
        {
            return new PrebuiltIndexResult(false, "The published index looks truncated; ignoring it.");
        }

        if (!force && !await IsNewerThanLocalAsync(manifest, ct))
        {
            return new PrebuiltIndexResult(false, "The local index is already current.");
        }

        Directory.CreateDirectory(options.StagingDirectory);
        var staging = Path.Combine(options.StagingDirectory, "embeddings.db.partial");
        try
        {
            await DownloadAndDecompressAsync(client, manifest, staging, ct);
            var rows = ValidateStaged(staging, manifest);

            // Re-check after the download: a pass may have started while we were fetching, and it
            // would be writing into the file we're about to replace.
            if (indexStatus.Running)
            {
                return new PrebuiltIndexResult(false, "An indexing pass started mid-download; discarded.");
            }

            await cache.SwapDatabaseAsync(staging, ct);
            await settings.SetAsync(
                SettingKeys.RecommendationsPrebuiltGeneratedAt,
                (manifest.GeneratedAt ?? DateTime.UtcNow).ToString("O", CultureInfo.InvariantCulture),
                ct);

            logger.LogInformation("Installed the prebuilt embedding index ({Rows} series)", rows);
            // Unformatted on purpose: the UI has the raw count and localizes it itself, and
            // server-side grouping picks up the host's locale (non-breaking spaces and all).
            return new PrebuiltIndexResult(true, $"Installed {rows} embedded series.", rows);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Prebuilt index install failed");
            return new PrebuiltIndexResult(false, $"Install failed: {ex.Message}");
        }
        finally
        {
            TryDelete(staging);
        }
    }

    /// <summary>
    /// True when the published artifact is newer than what we installed, or when the local index
    /// is too thin to be worth keeping. Never installs *older* than what's here — that would throw
    /// away work and move the user backwards.
    /// </summary>
    private async Task<bool> IsNewerThanLocalAsync(PrebuiltIndexManifest manifest, CancellationToken ct)
    {
        var localCount = store.Count();
        if (localCount < MinRows)
        {
            return true; // nothing worth keeping
        }

        var installedAt = await settings.GetAsync(SettingKeys.RecommendationsPrebuiltGeneratedAt, ct);
        if (DateTime.TryParse(
                installedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var installed) &&
            manifest.GeneratedAt is { } published &&
            published <= installed)
        {
            return false;
        }

        // A locally-built index that already covers more series than the artifact is better than
        // what's on offer; leave it alone.
        return manifest.RowCount > localCount;
    }

    private async Task DownloadAndDecompressAsync(
        HttpClient client, PrebuiltIndexManifest manifest, string staging, CancellationToken ct)
    {
        logger.LogInformation(
            "Downloading the prebuilt embedding index ({Size:N0} MB)…", manifest.SizeBytes / 1_000_000);

        using var response = await client.GetAsync(manifest.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        // Hash the compressed bytes as they stream past (same shape as the dump download), so a
        // truncated or tampered artifact is caught without buffering 70 MB twice.
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var network = await response.Content.ReadAsStreamAsync(ct))
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

    /// <summary>Opens the staged file and proves it is a usable index before it replaces the live one.</summary>
    private long ValidateStaged(string staging, PrebuiltIndexManifest manifest)
    {
        using var conn = new SqliteConnection($"Data Source={staging};Mode=ReadOnly;Pooling=False");
        conn.Open();

        using (var check = conn.CreateCommand())
        {
            check.CommandText = "PRAGMA quick_check";
            check.CommandTimeout = 600;
            var result = check.ExecuteScalar()?.ToString();
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"downloaded index failed its integrity check ({result})");
            }
        }

        using var stats = conn.CreateCommand();
        stats.CommandText = "SELECT COUNT(*), COALESCE(MIN(length(vec)), 0), COALESCE(MAX(length(vec)), 0) FROM series_vectors";
        stats.CommandTimeout = 600;
        using var reader = stats.ExecuteReader();
        reader.Read();
        var rows = reader.GetInt64(0);
        var minVec = reader.GetInt64(1);
        var maxVec = reader.GetInt64(2);

        if (rows < MinRows)
        {
            throw new InvalidOperationException($"downloaded index holds only {rows} vectors");
        }

        // Vectors are int8: one byte per dimension. Anything else means the manifest lied about
        // the format, and every row would be discarded at load time.
        if (minVec != maxVec || maxVec != options.Dimensions)
        {
            throw new InvalidOperationException(
                $"downloaded index has {minVec}-{maxVec} byte vectors, expected {options.Dimensions}");
        }

        // Not fatal - the count is the publisher's claim, not a contract - but a big shortfall
        // means we fetched something other than what was advertised.
        if (manifest.RowCount > 0 && rows < manifest.RowCount * 0.95)
        {
            throw new InvalidOperationException(
                $"downloaded index holds {rows} vectors, well short of the {manifest.RowCount} advertised");
        }

        return rows;
    }

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
