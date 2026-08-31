using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maki.Core.Configuration;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ZstdSharp;

namespace Maki.Metadata.Taste;

public record TasteVectorManifest
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; }

    [JsonPropertyName("itemCount")] public long ItemCount { get; init; }

    [JsonPropertyName("dimensions")] public int Dimensions { get; init; }

    [JsonPropertyName("trainedReaders")] public long TrainedReaders { get; init; }

    /// <summary>
    /// Which reader folds the artifact learned from. Anything but <c>all</c> is an EVALUATION build
    /// and must never be installed for real users: it is missing a quarter of the population on
    /// purpose. See <see cref="TasteVectorInstaller.ValidateStaged"/>.
    /// </summary>
    [JsonPropertyName("trainingFold")] public string? TrainingFold { get; init; }

    [JsonPropertyName("generatedAt")] public DateTime? GeneratedAt { get; init; }

    [JsonPropertyName("sizeBytes")] public long SizeBytes { get; init; }

    [JsonPropertyName("sha256")] public string? Sha256 { get; init; }

    [JsonPropertyName("url")] public string? Url { get; init; }
}

public record TasteVectorResult(bool Installed, string Reason, long? ItemCount = null);

/// <summary>
/// Downloads and installs the behavioural item vectors published alongside Maki. Shaped like
/// <see cref="CoRead.CoReadInstaller"/> for the same reasons: the artifact cannot be built locally
/// (it needs the co-read fetcher's multi-day working database, which does not ship), so downloading
/// is the only way an install gets the channel, and an absent artifact is a clean no-op.
///
/// <para>
/// <b>Two guards are not shared with the graph installers.</b> The first is the same privacy check,
/// and it matters as much here: these vectors are derived from <c>coread-graph.db</c>, which holds
/// millions of per-user reading rows, so a mispublish of the working database is the accident worth
/// preventing. The second is new. A fold-limited artifact, built by
/// <c>build-taste-vectors.cs --fold-out</c> so the eval can grade it honestly, is a perfectly valid
/// SQLite file that is silently missing a quarter of the population. It is refused outright, because
/// nothing about it looks wrong at runtime.
/// </para>
///
/// <para>
/// Unlike a graph, this artifact is loaded by <see cref="VectorIndexCache"/> as part of building the
/// index rather than by a cache of its own, so installing it invalidates the whole index instead of
/// swapping one file in. That costs a rebuild (seconds) on install and keeps the scan reading one
/// row-aligned array rather than a dictionary.
/// </para>
/// </summary>
public class TasteVectorInstaller(
    IHttpClientFactory httpClientFactory,
    TasteVectorOptions options,
    VectorIndexCache index,
    IAppSettings settings,
    ILogger<TasteVectorInstaller> logger)
{
    public const string HttpClientName = "taste-vectors";

    /// <summary>
    /// The schema this build understands. Bumped only when <c>item_vectors</c> changes shape in a
    /// way <see cref="VectorIndexCache"/> could not read; a newer artifact is refused, not half-read.
    /// </summary>
    public const int SupportedSchemaVersion = 1;

    /// <summary>A moving tag, so the manifest URL stays the same across republishes.</summary>
    public const string ReleaseTag = "taste-vectors-latest";

    public string DefaultManifestUrl =>
        $"https://github.com/OrbitMPGH/Maki/releases/download/{ReleaseTag}/manifest.json";

    /// <summary>
    /// Sanity floor: an artifact this small is a mispublish, not a small model. A real one carries
    /// ~95,000 vectors, so this only catches a truncated or half-written file.
    /// </summary>
    private const long MinItems = 1000;

    /// <summary>
    /// Tied to the channel's own kill switch: if recommendations may not use the vectors, there is
    /// nothing to be gained by downloading 14 MB of them.
    /// </summary>
    public async Task<bool> IsEnabledAsync(CancellationToken ct = default) =>
        await settings.GetAsync(SettingKeys.RecommendationsTasteVectors, ct) != "false";

    /// <summary>
    /// Installs the published vectors when they are compatible with this build and newer than what
    /// is already here. <paramref name="force"/> skips only the freshness check, never the
    /// compatibility or safety ones.
    /// </summary>
    public async Task<TasteVectorResult> InstallAsync(bool force = false, CancellationToken ct = default)
    {
        if (!await IsEnabledAsync(ct))
        {
            return new TasteVectorResult(false, "The behavioural channel is turned off.");
        }

        var manifestUrl = await settings.GetAsync(SettingKeys.RecommendationsTasteVectorsUrl, ct);
        manifestUrl = string.IsNullOrWhiteSpace(manifestUrl) ? DefaultManifestUrl : manifestUrl.Trim();

        var client = httpClientFactory.CreateClient(HttpClientName);
        TasteVectorManifest? manifest;
        try
        {
            // Deserialized from a string rather than the response bytes: a manifest written by a
            // Windows tool can carry a UTF-8 BOM, which byte-level JSON parsing rejects outright.
            var json = await client.GetStringAsync(manifestUrl, ct);
            manifest = JsonSerializer.Deserialize<TasteVectorManifest>(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Debug, not warning: until an artifact is published this is the normal state of every
            // install, and it must not fill logs with something nobody can act on.
            logger.LogDebug(ex, "Taste vector manifest unavailable at {Url}", manifestUrl);
            return new TasteVectorResult(false, "Could not read the behavioural vector manifest.");
        }

        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Url))
        {
            return new TasteVectorResult(false, "The behavioural vector manifest is malformed.");
        }

        if (manifest.SchemaVersion > SupportedSchemaVersion)
        {
            logger.LogInformation(
                "Ignoring the published taste vectors: schema {Theirs}, this build reads {Ours}",
                manifest.SchemaVersion, SupportedSchemaVersion);
            return new TasteVectorResult(false, "The published vectors use a newer schema than this build reads.");
        }

        if (manifest.ItemCount < MinItems)
        {
            return new TasteVectorResult(false, "The published vectors look truncated; ignoring them.");
        }

        // Checked from the manifest as well as from the file, so an evaluation build is not even
        // downloaded. The file-level check in ValidateStaged is the one that actually protects an
        // install, since a manifest can say anything.
        if (IsFoldLimited(manifest.TrainingFold))
        {
            logger.LogWarning(
                "Refusing the published taste vectors: trained on folds '{Folds}', not the whole population",
                manifest.TrainingFold);
            return new TasteVectorResult(false, "The published vectors are an evaluation build.");
        }

        if (!force && !await IsNewerThanLocalAsync(manifest, ct))
        {
            return new TasteVectorResult(false, "The local behavioural vectors are already current.");
        }

        Directory.CreateDirectory(options.StagingDirectory);
        var staging = Path.Combine(options.StagingDirectory, "taste-vectors.db.partial");
        try
        {
            await DownloadAndDecompressAsync(client, manifest, staging, ct);
            var items = ValidateStaged(staging);

            // No SwapDatabaseAsync to call: the vectors live inside the index, so the file is moved
            // into place and the index dropped. The next request rebuilds it.
            Directory.CreateDirectory(Path.GetDirectoryName(options.DatabasePath)!);
            File.Move(staging, options.DatabasePath, overwrite: true);
            index.Invalidate();

            await settings.SetAsync(
                SettingKeys.RecommendationsTasteVectorsGeneratedAt,
                (manifest.GeneratedAt ?? DateTime.UtcNow).ToString("O", CultureInfo.InvariantCulture),
                ct);

            logger.LogInformation("Installed behavioural vectors ({Items} series)", items);
            return new TasteVectorResult(true, $"Installed {items} behavioural vectors.", items);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Behavioural vector install failed");
            return new TasteVectorResult(false, $"Install failed: {ex.Message}");
        }
        finally
        {
            TryDelete(staging);
        }
    }

    /// <summary>
    /// True when the published artifact is newer than the one we installed, or when there is no
    /// usable local file at all. Never installs <em>older</em> than what is here.
    /// </summary>
    private async Task<bool> IsNewerThanLocalAsync(TasteVectorManifest manifest, CancellationToken ct)
    {
        if (!File.Exists(options.DatabasePath))
        {
            return true;
        }

        var installedAt = await settings.GetAsync(SettingKeys.RecommendationsTasteVectorsGeneratedAt, ct);
        if (!DateTime.TryParse(
                installedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var installed))
        {
            // A file is present that we did not put there, or a marker lost to a settings reset.
            // Installing over it is the right call: the published artifact is the known quantity.
            return true;
        }

        return manifest.GeneratedAt is not { } published || published > installed;
    }

    private async Task DownloadAndDecompressAsync(
        HttpClient client, TasteVectorManifest manifest, string staging, CancellationToken ct)
    {
        logger.LogInformation(
            "Downloading behavioural vectors ({Size:N0} MB)…", manifest.SizeBytes / 1_000_000);

        using var response = await client.GetAsync(manifest.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        // Hash the compressed bytes as they stream past, so a truncated or tampered artifact is
        // caught without buffering the file twice.
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

    /// <summary>Opens the staged file and proves it is usable before it replaces the live one.</summary>
    internal static long ValidateStaged(string staging)
    {
        using var conn = new SqliteConnection($"Data Source={staging};Mode=ReadOnly;Pooling=False");
        conn.Open();

        // PERSONAL DATA FIRST, BEFORE ANYTHING ELSE, and not skippable by force. These vectors are
        // derived from coread-graph.db, which holds one row per reader per series; that file sits in
        // the same folder on the machine that builds this and is the likeliest mispublish. Refusing
        // it here does not undo a publish, but it stops every install that would otherwise have
        // downloaded and kept a copy.
        using (var personal = conn.CreateCommand())
        {
            personal.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('user_entry', 'user_state', 'pending_user')";
            if (personal.ExecuteScalar() is long found && found > 0)
            {
                throw new InvalidOperationException(
                    "downloaded file holds per-user reading tables; this is the trainer's working "
                    + "database, not an export, and it must not be distributed");
            }
        }

        using (var shape = conn.CreateCommand())
        {
            shape.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'item_vectors'";
            if (shape.ExecuteScalar() is not long found || found == 0)
            {
                throw new InvalidOperationException("downloaded file has no 'item_vectors' table");
            }
        }

        // An evaluation build is a valid file that is quietly missing a quarter of the readers. It
        // would work, score slightly worse, and give nobody a reason to look.
        using (var fold = conn.CreateCommand())
        {
            fold.CommandText = "SELECT value FROM meta WHERE key = 'trainingFold'";
            if (IsFoldLimited(fold.ExecuteScalar()?.ToString()))
            {
                throw new InvalidOperationException(
                    "downloaded file is a fold-limited evaluation build, not a full model");
            }
        }

        int dimensions;
        using (var meta = conn.CreateCommand())
        {
            meta.CommandText = "SELECT value FROM meta WHERE key = 'dimensions'";
            if (meta.ExecuteScalar()?.ToString() is not { Length: > 0 } value
                || !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out dimensions)
                || dimensions <= 0)
            {
                throw new InvalidOperationException("downloaded file declares no usable dimension");
            }
        }

        using var stats = conn.CreateCommand();
        stats.CommandText = $"""
            SELECT COUNT(*),
                   COALESCE(SUM(CASE WHEN scale IS NULL OR NOT (scale > 0) THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN length(vec) != {dimensions} THEN 1 ELSE 0 END), 0)
            FROM item_vectors
            """;
        stats.CommandTimeout = 600;
        using var reader = stats.ExecuteReader();
        reader.Read();
        var items = reader.GetInt64(0);
        var badScales = reader.GetInt64(1);
        var badWidths = reader.GetInt64(2);

        if (items < MinItems)
        {
            throw new InvalidOperationException($"downloaded file holds only {items} vectors");
        }

        // `scale IS NULL OR NOT (scale > 0)`, never `<= 0`. SQLite has no NaN and stores one as
        // NULL, and a three-valued comparison against NULL yields NULL, which the CASE falls through
        // as false — so the naive form passes exactly the rows it is meant to catch. Scale 0 is also
        // this layer's "no vector" marker, so a stored 0 would make a row silently invisible.
        if (badScales > 0)
        {
            throw new InvalidOperationException($"downloaded file has {badScales} vectors with no usable scale");
        }

        if (badWidths > 0)
        {
            throw new InvalidOperationException(
                $"downloaded file has {badWidths} vectors that are not {dimensions} bytes wide");
        }

        return items;
    }

    /// <summary>
    /// Anything but <c>all</c> (or nothing at all, for an artifact written before the key existed)
    /// means a slice of readers was deliberately excluded.
    /// </summary>
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
            // A staged file we could not clean up is harmless; the next install overwrites it.
        }
    }
}
