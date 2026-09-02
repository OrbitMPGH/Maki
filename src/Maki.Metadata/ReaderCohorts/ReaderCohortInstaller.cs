using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maki.Core.Configuration;
using Maki.Metadata.MangaBaka;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ZstdSharp;

namespace Maki.Metadata.ReaderCohorts;

/// <summary>
/// What the published manifest says about a reader-cohort artifact. Field names match the JSON
/// written by <c>distribution/graph-artifact.cs</c>.
/// </summary>
public record ReaderCohortManifest
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; }

    [JsonPropertyName("cohortCount")] public int CohortCount { get; init; }

    [JsonPropertyName("cohortItemCount")] public long CohortItemCount { get; init; }

    [JsonPropertyName("itemCount")] public long ItemCount { get; init; }

    [JsonPropertyName("trainedReaders")] public long TrainedReaders { get; init; }

    [JsonPropertyName("trainingFold")] public string? TrainingFold { get; init; }

    [JsonPropertyName("generatedAt")] public DateTime? GeneratedAt { get; init; }

    [JsonPropertyName("sizeBytes")] public long SizeBytes { get; init; }

    [JsonPropertyName("sha256")] public string? Sha256 { get; init; }

    [JsonPropertyName("url")] public string? Url { get; init; }
}

/// <summary>Outcome of one install attempt, for logs and the settings UI.</summary>
public record ReaderCohortResult(bool Installed, string Reason, long? CohortItemCount = null);

/// <summary>
/// Downloads and installs the reader cohorts published alongside Maki. Shaped like
/// <see cref="CoRead.CoReadInstaller"/>, including the reasons: this artifact cannot be built
/// locally either (it needs the whole fetched reader population, which does not ship), so
/// downloading is the only way an install gets it, and an absent artifact has to stay a clean
/// no-op rather than an error.
///
/// <para>
/// <b>Two guards matter more here than the shape checks.</b> The cohorts are derived from
/// <c>coread-graph.db</c>, which sits in the same folder on the machine that builds this and holds
/// millions of per-user reading rows, so <see cref="ValidateStaged"/> refuses any file carrying a
/// per-user table before it looks at anything else. And an evaluation build — one deliberately
/// missing a quarter of the readers so the eval can grade honestly — installs, works, scores
/// slightly worse and gives nobody a reason to look, so its <c>trainingFold</c> is refused from the
/// manifest before the download and again from the file afterwards.
/// </para>
/// </summary>
public class ReaderCohortInstaller(
    IHttpClientFactory httpClientFactory,
    ReaderCohortOptions options,
    ReaderCohortCache cache,
    IAppSettings settings,
    ILogger<ReaderCohortInstaller> logger)
{
    public const string HttpClientName = "reader-cohorts";

    /// <summary>
    /// The schema this build understands. Bumped only when <c>cohort_item</c> or <c>item_global</c>
    /// change shape in a way <see cref="ReaderCohortCache"/> could not read; a newer artifact is
    /// refused rather than half-read.
    /// </summary>
    public const int SupportedSchemaVersion = 1;

    /// <summary>A moving tag, so the manifest URL stays the same across republishes.</summary>
    public const string ReleaseTag = "reader-cohorts-latest";

    public string DefaultManifestUrl =>
        $"https://github.com/OrbitMPGH/Maki/releases/download/{ReleaseTag}/manifest.json";

    /// <summary>
    /// Sanity floor: an artifact this small is a mispublish, not a small build. Well under the
    /// ~190,000 cohort rows a real one carries, so it only catches a truncated or half-written file.
    /// </summary>
    private const long MinCohortItems = 1000;

    /// <summary>
    /// Tied to the feature's own kill-switch: if nothing may read the cohorts, there is nothing to
    /// be gained by downloading them. Default on, matching how the switch reads elsewhere.
    /// </summary>
    public async Task<bool> IsEnabledAsync(CancellationToken ct = default) =>
        await settings.GetAsync(SettingKeys.RecommendationsReaderCohorts, ct) != "false";

    /// <summary>
    /// Installs the published cohorts when they are compatible with this build and newer than what
    /// is already here. <paramref name="force"/> skips only the freshness check (for a manual
    /// "download now"), never the compatibility or safety ones.
    /// </summary>
    public async Task<ReaderCohortResult> InstallAsync(bool force = false, CancellationToken ct = default)
    {
        if (!await IsEnabledAsync(ct))
        {
            return new ReaderCohortResult(false, "Reader cohorts are turned off.");
        }

        var manifestUrl = await settings.GetAsync(SettingKeys.RecommendationsReaderCohortsUrl, ct);
        manifestUrl = string.IsNullOrWhiteSpace(manifestUrl) ? DefaultManifestUrl : manifestUrl.Trim();

        var client = httpClientFactory.CreateClient(HttpClientName);
        ReaderCohortManifest? manifest;
        try
        {
            // Deserialized from a string rather than the response bytes: a manifest written by a
            // Windows tool can carry a UTF-8 BOM, which byte-level JSON parsing rejects outright.
            var json = await client.GetStringAsync(manifestUrl, ct);
            manifest = JsonSerializer.Deserialize<ReaderCohortManifest>(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Debug, not warning: until an artifact is published this is the normal state of every
            // install, and it must not fill logs with something nobody can act on.
            logger.LogDebug(ex, "Reader cohort manifest unavailable at {Url}", manifestUrl);
            return new ReaderCohortResult(false, "Could not read the reader cohort manifest.");
        }

        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Url))
        {
            return new ReaderCohortResult(false, "The reader cohort manifest is malformed.");
        }

        if (manifest.SchemaVersion > SupportedSchemaVersion)
        {
            logger.LogInformation(
                "Ignoring the published reader cohorts: schema {Theirs}, this build reads {Ours}",
                manifest.SchemaVersion, SupportedSchemaVersion);
            return new ReaderCohortResult(
                false, "The published cohorts use a newer schema than this build reads.");
        }

        if (manifest.CohortItemCount < MinCohortItems)
        {
            return new ReaderCohortResult(false, "The published cohorts look truncated; ignoring them.");
        }

        // Checked here so an evaluation build is not even downloaded, and again from the file in
        // ValidateStaged because a manifest can say anything.
        if (IsFoldLimited(manifest.TrainingFold))
        {
            logger.LogInformation(
                "Ignoring the published reader cohorts: trainingFold '{Fold}' is a fold-limited build",
                manifest.TrainingFold);
            return new ReaderCohortResult(
                false, "The published cohorts are an evaluation build, not a full one.");
        }

        if (!force && !await IsNewerThanLocalAsync(manifest, ct))
        {
            return new ReaderCohortResult(false, "The local reader cohorts are already current.");
        }

        Directory.CreateDirectory(options.StagingDirectory);
        var staging = Path.Combine(options.StagingDirectory, "reader-cohorts.db.partial");
        try
        {
            await DownloadAndDecompressAsync(client, manifest, staging, ct);
            var rows = ValidateStaged(staging, manifest);

            await cache.SwapDatabaseAsync(staging, ct);
            await settings.SetAsync(
                SettingKeys.RecommendationsReaderCohortsGeneratedAt,
                (manifest.GeneratedAt ?? DateTime.UtcNow).ToString("O", CultureInfo.InvariantCulture),
                ct);

            logger.LogInformation("Installed reader cohorts ({Rows} cohort rows)", rows);

            // Unformatted on purpose: the UI has the raw count and localizes it itself, and
            // server-side grouping picks up the host's locale (non-breaking spaces and all).
            return new ReaderCohortResult(true, $"Installed {rows} cohort rows.", rows);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Reader cohort install failed");
            return new ReaderCohortResult(false, $"Install failed: {ex.Message}");
        }
        finally
        {
            TryDelete(staging);
        }
    }

    /// <summary>
    /// A build that held a slice of readers out on purpose. "all" is the only full one; anything
    /// else names the folds it did see and is therefore missing readers by design.
    /// </summary>
    private static bool IsFoldLimited(string? trainingFold) =>
        !string.IsNullOrWhiteSpace(trainingFold)
        && !trainingFold.Trim().Equals("all", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the published artifact is newer than the one we installed, or when there is no
    /// usable local copy at all. Never installs <em>older</em> than what is here.
    /// </summary>
    private async Task<bool> IsNewerThanLocalAsync(ReaderCohortManifest manifest, CancellationToken ct)
    {
        if (!File.Exists(options.DatabasePath))
        {
            return true;
        }

        var installedAt = await settings.GetAsync(SettingKeys.RecommendationsReaderCohortsGeneratedAt, ct);
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
        HttpClient client, ReaderCohortManifest manifest, string staging, CancellationToken ct)
    {
        logger.LogInformation(
            "Downloading reader cohorts ({Size:N0} MB)…", manifest.SizeBytes / 1_000_000);

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
    private static long ValidateStaged(string staging, ReaderCohortManifest manifest)
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
                throw new InvalidOperationException($"downloaded file failed its integrity check ({result})");
            }
        }

        // FIRST, before any shape check. The cohorts are derived from coread-graph.db, which holds
        // one row per user per series read and sits in the same folder on the machine that builds
        // this. Refusing it here does not undo a publish, but it stops every install that would
        // otherwise have downloaded and kept a copy. Matched by prefix rather than by three exact
        // names, so a working table nobody remembered to list is caught too.
        using (var personal = conn.CreateCommand())
        {
            personal.CommandText =
                """
                SELECT COUNT(*) FROM sqlite_master
                WHERE type = 'table' AND (name LIKE 'user\_%' ESCAPE '\' OR name = 'pending_user')
                """;
            if (personal.ExecuteScalar() is long found && found > 0)
            {
                throw new InvalidOperationException(
                    "downloaded file holds per-user reading tables; this is the fetcher's working "
                    + "database, not an export, and it must not be distributed");
            }
        }

        foreach (var table in new[] { "cohort", "cohort_item", "item_global" })
        {
            using var shape = conn.CreateCommand();
            shape.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $n";
            shape.Parameters.AddWithValue("$n", table);
            if (shape.ExecuteScalar() is not long found || found == 0)
            {
                throw new InvalidOperationException($"downloaded file has no '{table}' table");
            }
        }

        // An evaluation build is a valid file that is quietly missing a quarter of the readers. It
        // installs, works, scores slightly worse and gives nobody a reason to look.
        using (var fold = conn.CreateCommand())
        {
            fold.CommandText = "SELECT value FROM meta WHERE key = 'trainingFold'";
            if (IsFoldLimited(fold.ExecuteScalar()?.ToString()))
            {
                throw new InvalidOperationException(
                    "downloaded file is a fold-limited evaluation build, not a full one");
            }
        }

        using var stats = conn.CreateCommand();
        stats.CommandText = """
            SELECT (SELECT COUNT(*) FROM cohort),
                   (SELECT COUNT(*) FROM cohort_item),
                   (SELECT COUNT(*) FROM item_global),
                   (SELECT COALESCE(SUM(CASE WHEN readers IS NULL OR readers <= 0 THEN 1 ELSE 0 END), 0) FROM cohort),
                   (SELECT COALESCE(SUM(CASE WHEN completions IS NULL OR completions <= 0 THEN 1 ELSE 0 END), 0) FROM cohort_item),
                   (SELECT COALESCE(SUM(CASE WHEN mean IS NOT NULL AND NOT (mean > 0 AND mean <= 100) THEN 1 ELSE 0 END), 0) FROM cohort_item)
            """;
        stats.CommandTimeout = 600;
        using var reader = stats.ExecuteReader();
        reader.Read();
        var cohorts = reader.GetInt64(0);
        var cohortRows = reader.GetInt64(1);
        var globalRows = reader.GetInt64(2);
        var emptyCohorts = reader.GetInt64(3);
        var emptyRows = reader.GetInt64(4);
        var badMeans = reader.GetInt64(5);

        if (cohorts is 0 or > byte.MaxValue)
        {
            throw new InvalidOperationException(
                $"downloaded file declares {cohorts} cohorts, which the row layout cannot carry");
        }

        if (cohortRows < MinCohortItems)
        {
            throw new InvalidOperationException($"downloaded file holds only {cohortRows} cohort rows");
        }

        if (globalRows == 0)
        {
            throw new InvalidOperationException("downloaded file has no global rows to divide cohort rates by");
        }

        if (emptyCohorts > 0)
        {
            throw new InvalidOperationException($"downloaded file has {emptyCohorts} cohorts with no readers");
        }

        if (emptyRows > 0)
        {
            throw new InvalidOperationException(
                $"downloaded file has {emptyRows} cohort rows with no completions behind them");
        }

        // `mean IS NOT NULL AND NOT (mean > 0 AND mean <= 100)` rather than a plain range test, for
        // the reason written down against `strength` and `scale`: SQLite stores a NaN as NULL, and a
        // three-valued comparison lets exactly the rows it is meant to catch through. NULL is
        // legitimate here — it is "finished often enough to count, rated too rarely to average" —
        // so it is excluded explicitly rather than by accident.
        if (badMeans > 0)
        {
            throw new InvalidOperationException(
                $"downloaded file has {badMeans} cohort means outside the 1-100 score range");
        }

        // Not fatal in principle - the count is the publisher's claim, not a contract - but a big
        // shortfall means we fetched something other than what was advertised.
        if (manifest.CohortItemCount > 0 && cohortRows < manifest.CohortItemCount * 0.95)
        {
            throw new InvalidOperationException(
                $"downloaded file holds {cohortRows} cohort rows, well short of the "
                + $"{manifest.CohortItemCount} advertised");
        }

        return cohortRows;
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
            // A staged file left behind is harmless: the next install overwrites it.
        }
    }
}
