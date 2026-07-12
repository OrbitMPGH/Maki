using System.Globalization;
using System.Text.Json;
using Mangarr.Metadata.MangaBaka;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Mangarr.Metadata.Embedding;

/// <summary>
/// Recommends series by semantic "feel": the seed vector (mean of the library's embeddings)
/// is compared by cosine against every embedded candidate, then re-ranked with the same
/// genre/tag/author/quality signals the v1 scorer used. Falls back to nothing when the
/// vector index isn't populated yet (the caller then uses the genre-only scorer).
/// </summary>
public class SemanticRecommender(
    MangaBakaDumpOptions dumpOptions,
    EmbeddingStore store,
    ILogger<SemanticRecommender> logger)
{
    private const double CosineFloor = 0.30; // below this, "feel" is too weak to recommend on
    private static readonly EmbeddingMath.Weights Weights = new();

    /// <summary>True once enough vectors exist to recommend from.</summary>
    public bool IsReady() => store.Count() >= 1000;

    public async Task<IReadOnlyList<MangaBakaRecommendation>> GetSimilarAsync(
        IReadOnlyCollection<long> libraryIds, IReadOnlyCollection<long> excludeIds,
        int limit, CancellationToken ct = default)
    {
        var seed = store.GetMeanVector(libraryIds);
        if (seed is null)
        {
            logger.LogInformation("Semantic reco skipped — no vectors for the library yet");
            return [];
        }

        using var conn = new SqliteConnection($"Data Source={store.DbPath};Pooling=False");
        conn.Open();
        using (var attach = conn.CreateCommand())
        {
            attach.CommandText = "ATTACH DATABASE $dump AS dump";
            attach.Parameters.AddWithValue("$dump", dumpOptions.DatabasePath);
            attach.ExecuteNonQuery();
        }

        var (genreWeight, tagWeight, authors) = await BuildProfileAsync(conn, libraryIds, ct);
        var exclude = new HashSet<long>(libraryIds.Concat(excludeIds));

        var top = new List<(double Score, MangaBakaRecommendation Item)>();
        var floor = double.NegativeInfinity;
        using (var scan = conn.CreateCommand())
        {
            scan.CommandText = """
                SELECT d.id, d.title, d.cover_raw_url, d.year, d.status, d.rating, d.total_chapters,
                       d.genres, d.tags, d.authors, v.vec
                FROM series_vectors v
                JOIN dump.series d ON d.id = v.id
                WHERE d.state = 'active' AND d.rating IS NOT NULL
                  AND d.content_rating != 'pornographic' AND d.type != 'novel'
                """;
            scan.CommandTimeout = 600;
            using var reader = await scan.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetInt64(0);
                if (exclude.Contains(id) || reader.GetValue(10) is not byte[] blob ||
                    EmbeddingMath.FromBlob(blob) is not { } vec)
                {
                    continue;
                }

                double cosine = EmbeddingMath.Cosine(seed, vec);
                if (cosine < CosineFloor)
                {
                    continue;
                }

                var matchedGenres = ParseStringArray(GetString(reader, 7))
                    .Where(genreWeight.ContainsKey).OrderByDescending(g => genreWeight[g]).ToList();
                var matchedTags = ParseStringArray(GetString(reader, 8))
                    .Where(tagWeight.ContainsKey).OrderByDescending(t => tagWeight[t]).ToList();
                var authorMatch = ParseStringArray(GetString(reader, 9)).Any(authors.Contains);
                var rating = reader.GetDouble(5);

                var score = EmbeddingMath.HybridScore(
                    cosine,
                    matchedGenres.Sum(g => genreWeight[g]),
                    matchedTags.Sum(t => tagWeight[t]),
                    authorMatch,
                    rating,
                    Weights);
                if (score <= floor)
                {
                    continue;
                }

                top.Add((score, new MangaBakaRecommendation(
                    id.ToString(CultureInfo.InvariantCulture),
                    GetString(reader, 1) ?? string.Empty,
                    GetString(reader, 2),
                    GetInt(reader, 3),
                    null, // description hydrated for winners below
                    MangaBakaProvider.MapStatus(GetString(reader, 4)),
                    rating,
                    ParseCount(GetString(reader, 6)),
                    matchedGenres.Take(4).ToList(),
                    matchedTags.Take(4).ToList(),
                    authorMatch,
                    null, null)));

                if (top.Count >= limit * 8)
                {
                    top = top.OrderByDescending(x => x.Score).Take(limit * 4).ToList();
                    floor = top[^1].Score;
                }
            }
        }

        var winners = top.OrderByDescending(x => x.Score).Take(limit).Select(x => x.Item).ToList();
        await HydrateDescriptionsAsync(conn, winners, ct);
        logger.LogInformation("Semantic reco returned {Count} of {Considered} scored candidates", winners.Count, top.Count);
        return winners;
    }

    private static async Task<(Dictionary<string, double> Genre, Dictionary<string, double> Tag, HashSet<string> Authors)>
        BuildProfileAsync(SqliteConnection conn, IReadOnlyCollection<long> libraryIds, CancellationToken ct)
    {
        var genreWeight = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var tagWeight = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var authors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (libraryIds.Count == 0)
        {
            return (genreWeight, tagWeight, authors);
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT genres, tags, authors FROM dump.series WHERE id IN ({string.Join(",", libraryIds)})";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            foreach (var g in ParseStringArray(GetString(reader, 0)))
            {
                genreWeight[g] = genreWeight.GetValueOrDefault(g) + 1.0 / libraryIds.Count;
            }

            foreach (var t in ParseStringArray(GetString(reader, 1)))
            {
                tagWeight[t] = tagWeight.GetValueOrDefault(t) + 1.0 / libraryIds.Count;
            }

            foreach (var a in ParseStringArray(GetString(reader, 2)))
            {
                authors.Add(a);
            }
        }

        return (genreWeight, tagWeight, authors);
    }

    private static async Task HydrateDescriptionsAsync(
        SqliteConnection conn, List<MangaBakaRecommendation> winners, CancellationToken ct)
    {
        if (winners.Count == 0)
        {
            return;
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT id, description FROM dump.series WHERE id IN ({string.Join(",", winners.Select(w => w.ProviderId))})";
        var descriptions = new Dictionary<string, string?>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            descriptions[reader.GetInt64(0).ToString(CultureInfo.InvariantCulture)] = GetString(reader, 1);
        }

        for (var i = 0; i < winners.Count; i++)
        {
            winners[i] = winners[i] with { Description = descriptions.GetValueOrDefault(winners[i].ProviderId) };
        }
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

    private static int? ParseCount(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
        {
            return whole;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var frac) ? (int)frac : null;
    }

    private static string? GetString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? GetInt(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
}
