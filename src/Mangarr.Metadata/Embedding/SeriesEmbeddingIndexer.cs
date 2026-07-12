using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mangarr.Metadata.MangaBaka;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Mangarr.Metadata.Embedding;

/// <summary>
/// Precomputes an embedding for every recommendable series in the local MangaBaka dump and
/// stores it in the vector DB. Only series whose text (title + genres + description) or model
/// version changed since last run are re-embedded, so nightly reruns are cheap. The first run
/// over the full dump is a one-time background pass (minutes on CPU).
/// </summary>
public class SeriesEmbeddingIndexer(
    MangaBakaDumpOptions dumpOptions,
    EmbeddingStore store,
    TextEmbedder embedder,
    ILogger<SeriesEmbeddingIndexer> logger)
{
    private const int BatchSize = 32;

    public record IndexResult(int Scanned, int Embedded, int Skipped);

    /// <summary>
    /// Runs the pass. <paramref name="limit"/> caps how many candidate rows are scanned
    /// (used by tests); null scans them all.
    /// </summary>
    public async Task<IndexResult> RunAsync(int? limit = null, CancellationToken ct = default)
    {
        if (!File.Exists(dumpOptions.DatabasePath))
        {
            logger.LogDebug("Embedding index skipped — MangaBaka dump not present");
            return new IndexResult(0, 0, 0);
        }

        if (!await embedder.EnsureReadyAsync(ct))
        {
            logger.LogWarning("Embedding index skipped — embedder not ready");
            return new IndexResult(0, 0, 0);
        }

        store.EnsureSchema();
        var existing = store.GetHashes();

        var scanned = 0;
        var skipped = 0;
        var embedded = 0;
        var pendingIds = new List<long>();
        var pendingHashes = new List<string>();
        var pendingTexts = new List<string>();

        using var conn = new SqliteConnection($"Data Source={dumpOptions.DatabasePath};Mode=ReadOnly;Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        // Only embed series we could actually recommend: active, rated, non-novel, safe, with a
        // real description. Matches SemanticRecommender's candidate filter, so no vector is wasted.
        cmd.CommandText = """
            SELECT id, title, genres, description
            FROM series
            WHERE state = 'active' AND rating IS NOT NULL
              AND content_rating != 'pornographic' AND type != 'novel'
              AND description IS NOT NULL AND length(description) > 20
            """ + (limit is { } n ? $" LIMIT {n}" : string.Empty);
        cmd.CommandTimeout = 600;

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            scanned++;
            var id = reader.GetInt64(0);
            var text = BuildText(GetString(reader, 1), GetString(reader, 2), GetString(reader, 3));
            var hash = Hash(text);
            if (existing.TryGetValue(id, out var stored) && stored == hash)
            {
                skipped++;
                continue;
            }

            pendingIds.Add(id);
            pendingHashes.Add(hash);
            pendingTexts.Add(text);

            if (pendingTexts.Count >= BatchSize)
            {
                embedded += Flush(pendingIds, pendingHashes, pendingTexts);
                if (embedded % 2048 == 0)
                {
                    logger.LogInformation("Embedding index progress: {Embedded} embedded, {Skipped} unchanged", embedded, skipped);
                }
            }
        }

        embedded += Flush(pendingIds, pendingHashes, pendingTexts);
        logger.LogInformation(
            "Embedding index done: scanned {Scanned}, embedded {Embedded}, unchanged {Skipped}",
            scanned, embedded, skipped);
        return new IndexResult(scanned, embedded, skipped);
    }

    private int Flush(List<long> ids, List<string> hashes, List<string> texts)
    {
        if (texts.Count == 0)
        {
            return 0;
        }

        var vectors = embedder.EmbedBatch(texts);
        var rows = new List<(long, string, float[])>(texts.Count);
        for (var i = 0; i < texts.Count; i++)
        {
            rows.Add((ids[i], hashes[i], vectors[i]));
        }

        store.UpsertBatch(rows);
        var n = texts.Count;
        ids.Clear();
        hashes.Clear();
        texts.Clear();
        return n;
    }

    /// <summary>Title + genres + description — the text whose "feel" we embed.</summary>
    internal static string BuildText(string? title, string? genresJson, string? description)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(title))
        {
            sb.Append(title).Append(". ");
        }

        var genres = ParseStringArray(genresJson);
        if (genres.Count > 0)
        {
            sb.Append("Genres: ").Append(string.Join(", ", genres)).Append(". ");
        }

        sb.Append(description);
        return sb.ToString();
    }

    private static string Hash(string text)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(EmbeddingOptions.ModelVersion + "\n" + text));
        return Convert.ToHexStringLower(bytes);
    }

    private static IReadOnlyList<string> ParseStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? GetString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
