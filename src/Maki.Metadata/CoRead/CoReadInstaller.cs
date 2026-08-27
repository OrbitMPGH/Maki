using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maki.Core.Configuration;
using Maki.Metadata.MangaBaka;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ZstdSharp;

namespace Maki.Metadata.CoRead;

/// <summary>
/// What the published manifest says about a co-read artifact. Field names match the JSON written
/// by <c>distribution/graph-artifact.cs</c>.
/// </summary>
public record CoReadManifest
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; }

    [JsonPropertyName("pairCount")] public long PairCount { get; init; }

    [JsonPropertyName("seriesCount")] public long SeriesCount { get; init; }

    [JsonPropertyName("userCount")] public long UserCount { get; init; }

    [JsonPropertyName("generatedAt")] public DateTime? GeneratedAt { get; init; }

    [JsonPropertyName("sizeBytes")] public long SizeBytes { get; init; }

    [JsonPropertyName("sha256")] public string? Sha256 { get; init; }

    [JsonPropertyName("url")] public string? Url { get; init; }
}

/// <summary>Outcome of one install attempt, for logs and the settings UI.</summary>
public record CoReadResult(bool Installed, string Reason, long? PairCount = null);

/// <summary>
/// Downloads and installs the co-read graph published alongside Maki. Deliberately shaped like
/// <see cref="RecoGraph.RecoGraphInstaller"/>, including the reasons: this artifact cannot be built
/// locally either (it is days of paced requests against AniList with a tool that does not ship with
/// the app), so downloading is the only way an install gets the channel, and an absent artifact has
/// to stay a clean no-op rather than an error.
///
/// <para>
/// <b>One guard is not shared, and it is the important one.</b> The co-read fetcher's working
/// database sits next to the artifact, has a near-identical name, and holds <c>user_entry</c> — the
/// per-user reading rows the whole pipeline exists to keep local. Publishing it by mistake would be
/// a privacy incident rather than a broken feature, so <see cref="ValidateStaged"/> refuses any file
/// carrying that table outright, before it is ever swapped in. That check is cheap, and it is the
/// last place the mistake can still be caught.
/// </para>
/// </summary>
public class CoReadInstaller(
    IHttpClientFactory httpClientFactory,
    CoReadOptions options,
    CoReadCache cache,
    IAppSettings settings,
    ILogger<CoReadInstaller> logger)
{
    public const string HttpClientName = "coread-graph";

    /// <summary>
    /// The schema this build understands. Bumped only when the <c>pair</c> table's shape changes in
    /// a way <c>CoReadCache</c> could not read; a newer artifact is refused rather than half-read.
    /// </summary>
    public const int SupportedSchemaVersion = 1;

    /// <summary>A moving tag, so the manifest URL stays the same across republishes.</summary>
    public const string ReleaseTag = "coread-graph-latest";

    public string DefaultManifestUrl =>
        $"https://github.com/OrbitMPGH/Maki/releases/download/{ReleaseTag}/manifest.json";

    /// <summary>
    /// Sanity floor: an artifact this small is a mispublish, not a small graph. Well under the
    /// ~1.18M pairs a real one carries, so it only catches a truncated or half-written file.
    /// </summary>
    private const long MinPairs = 1000;

    /// <summary>
    /// Tied to the channel's own kill-switch: if recommendations may not use the co-read graph,
    /// there is nothing to be gained by downloading it. Default on, matching how the switch reads
    /// elsewhere.
    /// </summary>
    public async Task<bool> IsEnabledAsync(CancellationToken ct = default) =>
        await settings.GetAsync(SettingKeys.RecommendationsCoRead, ct) != "false";

    /// <summary>
    /// Installs the published graph when it is compatible with this build and newer than what is
    /// already here. <paramref name="force"/> skips only the freshness check (for a manual
    /// "download now"), never the compatibility or safety ones.
    /// </summary>
    public async Task<CoReadResult> InstallAsync(bool force = false, CancellationToken ct = default)
    {
        if (!await IsEnabledAsync(ct))
        {
            return new CoReadResult(false, "The co-read channel is turned off.");
        }

        var manifestUrl = await settings.GetAsync(SettingKeys.RecommendationsCoReadUrl, ct);
        manifestUrl = string.IsNullOrWhiteSpace(manifestUrl) ? DefaultManifestUrl : manifestUrl.Trim();

        var client = httpClientFactory.CreateClient(HttpClientName);
        CoReadManifest? manifest;
        try
        {
            // Deserialized from a string rather than the response bytes: a manifest written by a
            // Windows tool can carry a UTF-8 BOM, which byte-level JSON parsing rejects outright.
            var json = await client.GetStringAsync(manifestUrl, ct);
            manifest = JsonSerializer.Deserialize<CoReadManifest>(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Debug, not warning: until an artifact is published this is the normal state of every
            // install, and it must not fill logs with something nobody can act on.
            logger.LogDebug(ex, "Co-read graph manifest unavailable at {Url}", manifestUrl);
            return new CoReadResult(false, "Could not read the co-read graph manifest.");
        }

        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Url))
        {
            return new CoReadResult(false, "The co-read graph manifest is malformed.");
        }

        if (manifest.SchemaVersion > SupportedSchemaVersion)
        {
            logger.LogInformation(
                "Ignoring the published co-read graph: schema {Theirs}, this build reads {Ours}",
                manifest.SchemaVersion, SupportedSchemaVersion);
            return new CoReadResult(false, "The published graph uses a newer schema than this build reads.");
        }

        if (manifest.PairCount < MinPairs)
        {
            return new CoReadResult(false, "The published graph looks truncated; ignoring it.");
        }

        if (!force && !await IsNewerThanLocalAsync(manifest, ct))
        {
            return new CoReadResult(false, "The local co-read graph is already current.");
        }

        Directory.CreateDirectory(options.StagingDirectory);
        var staging = Path.Combine(options.StagingDirectory, "coread-edges.db.partial");
        try
        {
            await DownloadAndDecompressAsync(client, manifest, staging, ct);
            var pairs = ValidateStaged(staging, manifest);

            await cache.SwapDatabaseAsync(staging, ct);
            await settings.SetAsync(
                SettingKeys.RecommendationsCoReadGeneratedAt,
                (manifest.GeneratedAt ?? DateTime.UtcNow).ToString("O", CultureInfo.InvariantCulture),
                ct);

            logger.LogInformation("Installed the co-read graph ({Pairs} pairs)", pairs);

            // Unformatted on purpose: the UI has the raw count and localizes it itself, and
            // server-side grouping picks up the host's locale (non-breaking spaces and all).
            return new CoReadResult(true, $"Installed {pairs} co-read pairs.", pairs);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Co-read graph install failed");
            return new CoReadResult(false, $"Install failed: {ex.Message}");
        }
        finally
        {
            TryDelete(staging);
        }
    }

    /// <summary>
    /// True when the published artifact is newer than the one we installed, or when there is no
    /// usable local graph at all. Never installs <em>older</em> than what is here.
    /// </summary>
    private async Task<bool> IsNewerThanLocalAsync(CoReadManifest manifest, CancellationToken ct)
    {
        if (!File.Exists(options.DatabasePath))
        {
            return true;
        }

        var installedAt = await settings.GetAsync(SettingKeys.RecommendationsCoReadGeneratedAt, ct);
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
        HttpClient client, CoReadManifest manifest, string staging, CancellationToken ct)
    {
        logger.LogInformation(
            "Downloading the co-read graph ({Size:N0} MB)…", manifest.SizeBytes / 1_000_000);

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

    /// <summary>Opens the staged file and proves it is a usable graph before it replaces the live one.</summary>
    private static long ValidateStaged(string staging, CoReadManifest manifest)
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
                throw new InvalidOperationException($"downloaded graph failed its integrity check ({result})");
            }
        }

        // The fetcher's working database is what this is most likely to be by mistake, and it holds
        // one row per user per series read. Refusing it here does not undo a publish, but it does
        // stop every install that would otherwise have downloaded and kept a copy, and it makes the
        // mistake loud instead of silent.
        using (var personal = conn.CreateCommand())
        {
            personal.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('user_entry', 'user_state', 'pending_user')";
            if (personal.ExecuteScalar() is long found && found > 0)
            {
                throw new InvalidOperationException(
                    "downloaded file holds per-user reading tables; this is the fetcher's working "
                    + "database, not an export, and it must not be distributed");
            }
        }

        using (var shape = conn.CreateCommand())
        {
            shape.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'pair'";
            if (shape.ExecuteScalar() is not long found || found == 0)
            {
                throw new InvalidOperationException("downloaded file has no 'pair' table");
            }
        }

        using var stats = conn.CreateCommand();
        stats.CommandText = """
            SELECT COUNT(*),
                   COALESCE(SUM(CASE WHEN a_id = b_id THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN strength IS NULL OR NOT (strength > 0) THEN 1 ELSE 0 END), 0)
            FROM pair
            """;
        stats.CommandTimeout = 600;
        using var reader = stats.ExecuteReader();
        reader.Read();
        var pairs = reader.GetInt64(0);
        var selfPairs = reader.GetInt64(1);
        var badStrengths = reader.GetInt64(2);

        if (pairs < MinPairs)
        {
            throw new InvalidOperationException($"downloaded graph holds only {pairs} pairs");
        }

        if (selfPairs > 0)
        {
            throw new InvalidOperationException($"downloaded graph has {selfPairs} self-pairs");
        }

        // Written as `strength IS NULL OR NOT (strength > 0)` rather than `<= 0`, because a
        // three-valued comparison against NULL yields NULL and the CASE would fall through to 0 —
        // the row would pass. NULL is the case that actually occurs: SQLite has no NaN, it stores
        // one as NULL, and CoReadCache would then read the edge as weightless rather than as
        // broken. A zero or negative strength is the other half; both mean the file was not
        // produced by the exporter.
        if (badStrengths > 0)
        {
            throw new InvalidOperationException(
                $"downloaded graph has {badStrengths} missing or non-positive strengths");
        }

        // Not fatal in principle - the count is the publisher's claim, not a contract - but a big
        // shortfall means we fetched something other than what was advertised.
        if (manifest.PairCount > 0 && pairs < manifest.PairCount * 0.95)
        {
            throw new InvalidOperationException(
                $"downloaded graph holds {pairs} pairs, well short of the {manifest.PairCount} advertised");
        }

        return pairs;
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
