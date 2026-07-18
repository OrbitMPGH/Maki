using System.Globalization;
using System.Text.Json;
using Maki.Metadata.MangaBaka;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Maki.Metadata.Embedding;

/// <summary>
/// Recommends series by semantic "feel": the seed vector (mean of the library's embeddings)
/// is compared by cosine against every embedded candidate, then re-ranked with genre/author/
/// quality signals plus the weighted-tag cosine (<see cref="TagMath"/>, from tags_v2).
/// Falls back to nothing when the vector index isn't populated yet (the caller then uses
/// the genre-only scorer).
/// </summary>
public class SemanticRecommender(
    MangaBakaDumpOptions dumpOptions,
    EmbeddingStore store,
    ILogger<SemanticRecommender> logger)
{
    private const double CosineFloor = 0.30; // below this, "feel" is too weak to recommend on
    private static readonly EmbeddingMath.Weights Weights = new();

    private long _maxPopularity; // cached global popularity rank ceiling (0 = not computed)
    private long _activeCount; // cached count of active dump series, the N in idf = log(N/df)

    /// <summary>True once enough vectors exist to recommend from.</summary>
    public bool IsReady() => store.Count() >= 1000;

    /// <summary>Global max popularity rank, used to turn a rank into a percentile. Cached per process.</summary>
    private async Task<long> GetMaxPopularityAsync(SqliteConnection conn, CancellationToken ct)
    {
        if (_maxPopularity > 0)
        {
            return _maxPopularity;
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(popularity_global_current) FROM dump.series";
        cmd.CommandTimeout = 600;
        _maxPopularity = await cmd.ExecuteScalarAsync(ct) is long l && l > 0 ? l : 300000;
        return _maxPopularity;
    }

    /// <summary>Count of active dump series — the corpus size N for tag IDF. Cached per process.</summary>
    private async Task<long> GetActiveCountAsync(SqliteConnection conn, CancellationToken ct)
    {
        if (_activeCount > 0)
        {
            return _activeCount;
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM dump.series WHERE state = 'active'";
        cmd.CommandTimeout = 600;
        _activeCount = await cmd.ExecuteScalarAsync(ct) is long l && l > 0 ? l : 300000;
        return _activeCount;
    }

    public async Task<IReadOnlyList<MangaBakaRecommendation>> GetSimilarAsync(
        IReadOnlyCollection<long> seedIds, IReadOnlyCollection<long> excludeIds,
        int limit, RecommendationFilters? filters = null, double obscurity = 0,
        IReadOnlyDictionary<long, double>? seedWeights = null,
        CancellationToken ct = default)
    {
        filters ??= RecommendationFilters.None;
        obscurity = Math.Clamp(obscurity, -1, 1);
        store.EnsureSchema(); // older DBs predate the tag tables the scan joins below
        // Seed vector is weighted by the user's ratings when supplied, so highly-rated library
        // titles pull the "feel" harder than unrated ones (genre/tag/author sub-profiles below
        // stay unweighted this pass).
        var seed = store.GetMeanVector(seedIds, seedWeights);
        if (seed is null)
        {
            logger.LogInformation("Semantic reco skipped — no vectors for the seeds yet");
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

        var (genreWeight, authors) = await BuildProfileAsync(conn, seedIds, ct);

        // Weighted tag channel: sparse IDF-weighted cosine between the seeds' tag profile and
        // each candidate's packed tags (see TagMath). Vocab gives names, spoiler flags, and
        // per-tag document frequency for the IDF.
        var vocab = store.GetVocab();
        var activeCount = await GetActiveCountAsync(conn, ct);
        double Idf(int tagId) =>
            vocab.TryGetValue(tagId, out var info) && info.SeriesCount > 0
                ? Math.Log((double)activeCount / info.SeriesCount)
                : 1.0;
        var tagProfile = TagMath.BuildProfile(store.GetTagBlobs(seedIds).Values, Idf);
        // Tag filter: each selected name maps to its vocab id(s) (case-insensitive — casing
        // variants map to distinct ids); a candidate must carry every selected tag. An unknown
        // name can never match, so bail out early.
        List<int[]>? requiredTagIds = null;
        if (filters.Tags is { Count: > 0 } wantedTags)
        {
            requiredTagIds = wantedTags
                .Select(name => vocab
                    .Where(kv => string.Equals(kv.Value.Name, name, StringComparison.OrdinalIgnoreCase))
                    .Select(kv => kv.Key)
                    .ToArray())
                .ToList();
            if (requiredTagIds.Any(ids => ids.Length == 0))
            {
                return [];
            }
        }
        // Per-seed vectors + titles, so each winner can be attributed to the one seed whose
        // "feel" drove it ("Feels like X"). Titles come from the dump; vectors from the store.
        var seedVectors = store.GetVectors(seedIds);
        var seedTitles = await GetTitlesAsync(conn, seedVectors.Keys, ct);
        var seedProfiles = seedVectors
            .Where(kv => seedTitles.ContainsKey(kv.Key))
            .Select(kv => (Title: seedTitles[kv.Key], Vec: kv.Value))
            .ToList();
        var exclude = new HashSet<long>(seedIds.Concat(excludeIds));
        // popularity_global_current is a global rank (1 = most popular). Normalize to a percentile
        // for the obscurity term; only needed when the dial is off-centre.
        var maxPopularity = obscurity != 0 ? await GetMaxPopularityAsync(conn, ct) : 1;
        var logMaxPopularity = Math.Log(Math.Max(2, maxPopularity));

        var top = new List<(double Score, MangaBakaRecommendation Item, float[] Vec)>();
        var floor = double.NegativeInfinity;
        using (var scan = conn.CreateCommand())
        {
            scan.CommandText = """
                SELECT d.id, d.title, d.cover_raw_url, d.year, d.status, d.rating, d.total_chapters,
                       d.genres, t.tags, d.authors, v.vec, d.popularity_global_current
                FROM series_vectors v
                LEFT JOIN series_tags t ON t.id = v.id
                JOIN dump.series d ON d.id = v.id
                WHERE d.state = 'active' AND d.rating IS NOT NULL
                  AND d.content_rating != 'pornographic' AND d.type != 'novel'
                """ + filters.BuildClause(scan, "d");
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

                if (requiredTagIds is not null &&
                    !TagMath.ContainsAll(reader.GetValue(8) as byte[], requiredTagIds))
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
                var matchedContributions = new List<(int Id, double Contribution)>();
                var tagScore = TagMath.Score(
                    reader.GetValue(8) as byte[], tagProfile, Idf, matchedContributions);
                // Strongest shared tags for the UI — never spoilers, ranked by how much they
                // actually moved the score.
                var matchedTags = matchedContributions
                    .OrderByDescending(m => m.Contribution)
                    .Select(m => vocab.TryGetValue(m.Id, out var info) && !info.IsSpoiler ? info.Name : null)
                    .OfType<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var authorMatch = ParseStringArray(GetString(reader, 9)).Any(authors.Contains);
                var rating = reader.GetDouble(5);
                // Obscurity percentile: 0 = most popular, 1 = most obscure. popularity_global_current
                // is a rank whose "fame" is roughly log-distributed — most good candidates cluster at
                // rank < 2000, so a linear percentile barely separates them. Log-scaling the rank
                // spreads that popular cluster out so the dial can actually reorder it.
                var rank = reader.IsDBNull(11) ? maxPopularity : Math.Max(1, reader.GetInt64(11));
                var percentile = obscurity == 0
                    ? 0.5
                    : Math.Clamp(Math.Log(rank) / logMaxPopularity, 0, 1);

                var score = EmbeddingMath.HybridScore(
                    cosine,
                    matchedGenres.Sum(g => genreWeight[g]),
                    tagScore,
                    authorMatch,
                    rating,
                    obscurity,
                    percentile,
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
                    null, null), vec));

                if (top.Count >= limit * 8)
                {
                    top = top.OrderByDescending(x => x.Score).Take(limit * 4).ToList();
                    floor = top[^1].Score;
                }
            }
        }

        // Attribute each winner to the single seed whose feel drove it, so the UI can say
        // "Feels like X" naming the specific title rather than the whole seed set.
        var seedVecList = seedProfiles.Select(p => p.Vec).ToList();
        var winners = top.OrderByDescending(x => x.Score).Take(limit).Select(x =>
        {
            var i = EmbeddingMath.MostSimilar(x.Vec, seedVecList);
            return i >= 0 ? x.Item with { BecauseOfTitle = seedProfiles[i].Title } : x.Item;
        }).ToList();
        await HydrateDescriptionsAsync(conn, winners, ct);
        logger.LogInformation("Semantic reco returned {Count} of {Considered} scored candidates", winners.Count, top.Count);
        return winners;
    }

    private static async Task<(Dictionary<string, double> Genre, HashSet<string> Authors)>
        BuildProfileAsync(SqliteConnection conn, IReadOnlyCollection<long> libraryIds, CancellationToken ct)
    {
        var genreWeight = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var authors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (libraryIds.Count == 0)
        {
            return (genreWeight, authors);
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT genres, authors FROM dump.series WHERE id IN ({string.Join(",", libraryIds)})";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            foreach (var g in ParseStringArray(GetString(reader, 0)))
            {
                genreWeight[g] = genreWeight.GetValueOrDefault(g) + 1.0 / libraryIds.Count;
            }

            foreach (var a in ParseStringArray(GetString(reader, 1)))
            {
                authors.Add(a);
            }
        }

        return (genreWeight, authors);
    }

    private static async Task<Dictionary<long, string>> GetTitlesAsync(
        SqliteConnection conn, IReadOnlyCollection<long> ids, CancellationToken ct)
    {
        var titles = new Dictionary<long, string>();
        if (ids.Count == 0)
        {
            return titles;
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT id, title FROM dump.series WHERE id IN ({string.Join(",", ids)})";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (GetString(reader, 1) is { Length: > 0 } title)
            {
                titles[reader.GetInt64(0)] = title;
            }
        }

        return titles;
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
