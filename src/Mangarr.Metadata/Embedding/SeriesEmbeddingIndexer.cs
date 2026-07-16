using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mangarr.Metadata.MangaBaka;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Mangarr.Metadata.Embedding;

/// <summary>
/// Precomputes an embedding for every recommendable series in the local MangaBaka dump and
/// stores it in the vector DB, along with its packed weighted-tag blob and the tag vocabulary
/// (from tags_v2). Only series whose text (title + genres + themes + description), tags, or
/// model version changed since last run are re-embedded, so nightly reruns are cheap. The
/// first run over the full dump is a one-time background pass (minutes on CPU).
/// </summary>
public class SeriesEmbeddingIndexer(
    MangaBakaDumpOptions dumpOptions,
    EmbeddingStore store,
    TextEmbedder embedder,
    EmbeddingIndexStatus status,
    ILogger<SeriesEmbeddingIndexer> logger)
{
    private const int BatchSize = 32;

    /// <summary>Candidate filter shared by the count and the scan — must stay in sync.</summary>
    private const string CandidateWhere =
        "state = 'active' AND rating IS NOT NULL AND content_rating != 'pornographic' " +
        "AND type != 'novel' AND description IS NOT NULL AND length(description) > 20";

    public record IndexResult(int Scanned, int Embedded, int Skipped);

    /// <summary>How many series are recommendable (and therefore get embedded). One scan; cache it.</summary>
    public async Task<int> CountRecommendableAsync(CancellationToken ct = default)
    {
        if (!File.Exists(dumpOptions.DatabasePath))
        {
            return 0;
        }

        using var conn = new SqliteConnection($"Data Source={dumpOptions.DatabasePath};Mode=ReadOnly;Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM series WHERE {CandidateWhere}";
        cmd.CommandTimeout = 600;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

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

        status.Begin();
        try
        {
            if (!await embedder.EnsureReadyAsync(ct))
            {
                logger.LogWarning("Embedding index skipped — embedder not ready");
                status.End(0, 0, "Embedding model not available");
                return new IndexResult(0, 0, 0);
            }

            status.SetPhase("indexing");
            if (limit is null)
            {
                status.SetTotal(await CountRecommendableAsync(ct));
            }

            store.EnsureSchema();
            var existing = store.GetHashes();
            var tagged = store.GetTaggedIds();

            var scanned = 0;
            var skipped = 0;
            var embedded = 0;
            var pendingIds = new List<long>();
            var pendingHashes = new List<string>();
            var pendingTexts = new List<string>();
            var pendingTags = new List<byte[]>();
            var tagBackfill = new List<(long Id, byte[] Tags)>(); // unchanged text, missing tag row
            var vocab = new Dictionary<int, TagInfo>();

            using var conn = new SqliteConnection($"Data Source={dumpOptions.DatabasePath};Mode=ReadOnly;Pooling=False");
            conn.Open();
            using var cmd = conn.CreateCommand();
            // Only embed series we could actually recommend (see CandidateWhere) — matches
            // SemanticRecommender's candidate filter, so no vector is wasted.
            cmd.CommandText =
                $"SELECT id, title, genres, description, tags_v2 FROM series WHERE {CandidateWhere}"
                + (limit is { } n ? $" LIMIT {n}" : string.Empty);
            cmd.CommandTimeout = 600;

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                scanned++;
                var id = reader.GetInt64(0);
                var tags = ParseTags(GetString(reader, 4));
                foreach (var t in tags)
                {
                    vocab.TryAdd(t.Id, new TagInfo(t.Name, t.SeriesCount, t.IsSpoiler));
                }

                var tagBlob = TagMath.Pack(tags.Select(t => (t.Id, t.Class)).ToList());
                var text = BuildText(GetString(reader, 1), GetString(reader, 2), SelectThemes(tags), GetString(reader, 3));
                var hash = Hash(text, tagBlob);
                if (existing.TryGetValue(id, out var stored) && stored == hash)
                {
                    skipped++;
                    if (!tagged.Contains(id))
                    {
                        tagBackfill.Add((id, tagBlob));
                    }

                    continue;
                }

                pendingIds.Add(id);
                pendingHashes.Add(hash);
                pendingTexts.Add(text);
                pendingTags.Add(tagBlob);

                if (pendingTexts.Count >= BatchSize)
                {
                    embedded += Flush(pendingIds, pendingHashes, pendingTexts, pendingTags);
                    status.Report(scanned, embedded);
                    if (embedded % 2048 == 0)
                    {
                        logger.LogInformation("Embedding index progress: {Embedded} embedded, {Skipped} unchanged", embedded, skipped);
                    }
                }
            }

            embedded += Flush(pendingIds, pendingHashes, pendingTexts, pendingTags);
            store.UpsertTagsBatch(tagBackfill);
            store.UpsertVocab(vocab);
            status.Report(scanned, embedded);
            logger.LogInformation(
                "Embedding index done: scanned {Scanned}, embedded {Embedded}, unchanged {Skipped}",
                scanned, embedded, skipped);
            status.End(embedded, skipped, null);
            return new IndexResult(scanned, embedded, skipped);
        }
        catch (Exception ex)
        {
            status.End(0, 0, ex is OperationCanceledException ? "Cancelled" : ex.Message);
            throw;
        }
    }

    private int Flush(List<long> ids, List<string> hashes, List<string> texts, List<byte[]> tags)
    {
        if (texts.Count == 0)
        {
            return 0;
        }

        var vectors = embedder.EmbedBatch(texts);
        var rows = new List<(long, string, float[])>(texts.Count);
        var tagRows = new List<(long, byte[])>(texts.Count);
        for (var i = 0; i < texts.Count; i++)
        {
            rows.Add((ids[i], hashes[i], vectors[i]));
            tagRows.Add((ids[i], tags[i]));
        }

        store.UpsertBatch(rows);
        store.UpsertTagsBatch(tagRows);
        var n = texts.Count;
        ids.Clear();
        hashes.Clear();
        texts.Clear();
        tags.Clear();
        return n;
    }

    internal sealed record ParsedTag(int Id, string Name, byte Class, bool IsSpoiler, long SeriesCount);

    /// <summary>Parses the tags_v2 JSON array; tolerant of missing fields and bad JSON.</summary>
    internal static List<ParsedTag> ParseTags(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            var tags = new List<ParsedTag>();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return tags;
            }

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object ||
                    !el.TryGetProperty("id", out var idProp) || !idProp.TryGetInt32(out var id) ||
                    !el.TryGetProperty("name", out var nameProp) || nameProp.GetString() is not { Length: > 0 } name)
                {
                    continue;
                }

                var cls = TagMath.ClassOf(
                    el.TryGetProperty("weight", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() : null);
                var spoiler = el.TryGetProperty("is_spoiler", out var s) && s.ValueKind == JsonValueKind.True;
                var count = el.TryGetProperty("series_count", out var c) && c.TryGetInt64(out var sc) ? sc : 0;
                tags.Add(new ParsedTag(id, name, cls, spoiler, count));
            }

            return tags;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The tags worth naming in the embedded text: non-spoiler core/defining tags, strongest
    /// first, capped so the description still dominates the token budget.
    /// </summary>
    internal static IReadOnlyList<string> SelectThemes(IReadOnlyList<ParsedTag> tags) =>
        tags.Where(t => !t.IsSpoiler && t.Class >= TagMath.Defining)
            .OrderByDescending(t => t.Class)
            .Take(10)
            .Select(t => t.Name)
            .ToList();

    /// <summary>Title + genres + themes + description — the text whose "feel" we embed.</summary>
    internal static string BuildText(string? title, string? genresJson, IReadOnlyList<string> themes, string? description)
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

        if (themes.Count > 0)
        {
            sb.Append("Themes: ").Append(string.Join(", ", themes)).Append(". ");
        }

        sb.Append(description);
        return sb.ToString();
    }

    private static string Hash(string text, byte[] tagBlob)
    {
        // The tag blob is part of the hash so tag-only changes (which don't alter the themes
        // clause) still refresh the stored tag row on the next pass.
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(
            EmbeddingOptions.ModelVersion + "\n" + text + "\n" + Convert.ToHexStringLower(SHA1.HashData(tagBlob))));
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
