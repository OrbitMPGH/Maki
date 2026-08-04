using System.Globalization;
using System.Text.Json;
using Maki.Metadata.MangaBaka;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Maki.Metadata.Embedding;

/// <summary>
/// Recommends series by semantic "feel", scored against the in-memory <see cref="VectorIndex"/>
/// rather than by re-reading every vector out of SQLite: the index already holds the same
/// candidate set (its <c>CandidateWhere</c> is this scan's old WHERE clause), already int8-packed
/// and already filterable, so the pass is a few tens of milliseconds instead of the seconds a
/// BLOB-per-row scan took. Winners are then hydrated from the dump in one query, which is the only
/// part that still needs it.
///
/// <para>
/// Retrieval is <em>multi-seed</em>. A single mean over the whole library is a lie about anybody
/// whose taste is not one thing — the centroid of One Piece and Berserk sits somewhere near
/// neither, and candidates that are an excellent match for one of them score badly against it. So
/// the library is queried once per representative seed <em>and</em> once from the weighted
/// centroid, and the rankings are fused by reciprocal rank fusion to decide who enters the scored
/// pool. The final score keeps the seed↔candidate cosine (the best one across the queries), not
/// the RRF value: every channel here is a cosine on one unit sphere, so they are already
/// calibrated against each other, and collapsing them to ranks would throw away the magnitude
/// <see cref="EmbeddingMath.HybridScore"/>'s weights are tuned against. RRF is doing what it is
/// good at — deciding <em>who is considered</em> — and nothing else.
/// </para>
///
/// Falls back to nothing when the index isn't built yet (the caller then uses the genre-only
/// scorer).
/// </summary>
public class SemanticRecommender(
    EmbeddingOptions options,
    MangaBakaDumpOptions dumpOptions,
    EmbeddingStore store,
    VectorIndexCache cache,
    ILogger<SemanticRecommender> logger)
{
    private const double CosineFloor = 0.30; // below this, "feel" is too weak to recommend on
    private static readonly EmbeddingMath.Weights Weights = new();

    /// <summary>Standard RRF damping, same constant the search fusion uses.</summary>
    private const double RrfK = 60;

    /// <summary>
    /// How many individual seeds get their own query, on top of the centroid. Each one costs
    /// another dot product per catalogue row, so this is the knob that decides how much a large
    /// library costs; the seeds chosen are the most spread-out ones (see
    /// <see cref="PickRepresentativeSeeds"/>), because near-duplicates would return the same
    /// candidates and buy nothing.
    /// </summary>
    private const int MaxSeedQueries = 8;

    private long _maxPopularity; // cached global popularity rank ceiling (0 = not computed)
    private long _activeCount; // cached count of active dump series, the N in idf = log(N/df)

    /// <summary>True once embeddings are on and enough vectors exist to recommend from.</summary>
    public bool IsReady() => options.Enabled && store.Count() >= 1000;

    /// <summary>One query vector, packed for the integer dot path. <see cref="SeedTitle"/> is null for the centroid.</summary>
    private sealed record SeedQuery(sbyte[] Packed, float Scale, string? SeedTitle);

    /// <summary>A scored candidate, carrying what hydration would otherwise have to recompute.</summary>
    private sealed record Candidate(int Row, double Score, int BestQuery, bool AuthorMatch);

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

    /// <param name="diversity">
    /// MMR's diversity weight ∈ [0,1]. 0 is the plain relevance order, so the default changes
    /// nobody's results; higher values trade a little relevance for picks that are not near-copies
    /// of each other.
    /// </param>
    public async Task<IReadOnlyList<MangaBakaRecommendation>> GetSimilarAsync(
        IReadOnlyCollection<long> seedIds, IReadOnlyCollection<long> excludeIds,
        int limit, RecommendationFilters? filters = null, double obscurity = 0,
        IReadOnlyDictionary<long, double>? seedWeights = null, double diversity = 0,
        CancellationToken ct = default)
    {
        filters ??= RecommendationFilters.None;
        obscurity = Math.Clamp(obscurity, -1, 1);
        diversity = Math.Clamp(diversity, 0, 1);
        store.EnsureSchema(); // older DBs predate the tag tables the index build joins

        var index = await cache.GetAsync(ct);
        if (index is null || index.Count == 0)
        {
            logger.LogInformation("Semantic reco skipped — the vector index isn't built yet");
            return [];
        }

        var plan = index.Plan(filters);
        if (plan.Impossible)
        {
            return [];
        }

        var seedVectors = store.GetVectors(seedIds);
        if (seedVectors.Count == 0)
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

        // The genre/author profile still comes from the dump rather than the index: a seed can be
        // a series the index doesn't carry (unrated, a novel, a merged row), and its genres should
        // still shape the profile even though it can never be a candidate itself.
        var (genreWeight, authors) = await BuildProfileAsync(conn, seedIds, ct);
        var genreWeightById = new Dictionary<int, double>(genreWeight.Count);
        foreach (var (name, weight) in genreWeight)
        {
            if (index.TryGetGenreId(name, out var id))
            {
                genreWeightById[id] = genreWeightById.GetValueOrDefault(id) + weight;
            }
        }

        var authorIds = new HashSet<int>();
        foreach (var name in authors)
        {
            if (index.TryGetAuthorId(name, out var id))
            {
                authorIds.Add(id);
            }
        }

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

        var seedTitles = await GetTitlesAsync(conn, seedVectors.Keys, ct);
        var queries = BuildQueries(seedVectors, seedWeights, seedTitles);
        if (queries.Count == 0)
        {
            return [];
        }

        var exclude = new HashSet<long>(seedIds.Concat(excludeIds));
        // popularity_global_current is a global rank (1 = most popular). Normalize to a percentile
        // for the obscurity term; only needed when the dial is off-centre.
        var maxPopularity = obscurity != 0 ? await GetMaxPopularityAsync(conn, ct) : 1;
        var logMaxPopularity = Math.Log(Math.Max(2, maxPopularity));

        var started = DateTime.UtcNow;
        var cosines = Scan(index, plan, queries, exclude, requiredTagIds, ct);
        var pooled = FuseByRank(cosines, Math.Clamp(limit * 4, 200, 2000));

        var scored = new List<Candidate>(pooled.Count);
        foreach (var row in pooled)
        {
            var bestQuery = 0;
            var bestCosine = double.NegativeInfinity;
            for (var q = 0; q < queries.Count; q++)
            {
                if (cosines[q][row] > bestCosine)
                {
                    bestCosine = cosines[q][row];
                    bestQuery = q;
                }
            }

            if (bestCosine < CosineFloor)
            {
                continue;
            }

            var genreSum = 0.0;
            foreach (var g in index.GenresAt(row))
            {
                genreSum += genreWeightById.GetValueOrDefault(g);
            }

            var authorMatch = false;
            foreach (var a in index.AuthorsAt(row))
            {
                if (authorIds.Contains(a))
                {
                    authorMatch = true;
                    break;
                }
            }

            // Obscurity percentile: 0 = most popular, 1 = most obscure. popularity_global_current
            // is a rank whose "fame" is roughly log-distributed — most good candidates cluster at
            // rank < 2000, so a linear percentile barely separates them. Log-scaling the rank
            // spreads that popular cluster out so the dial can actually reorder it.
            var storedRank = index.PopularityAt(row);
            var rank = storedRank == VectorIndex.Unknown ? maxPopularity : Math.Max(1, storedRank);
            var percentile = obscurity == 0
                ? 0.5
                : Math.Clamp(Math.Log(rank) / logMaxPopularity, 0, 1);

            var score = EmbeddingMath.HybridScore(
                bestCosine,
                genreSum,
                TagMath.Score(index.TagsAt(row), tagProfile, Idf),
                authorMatch,
                index.RatingAt(row),
                obscurity,
                percentile,
                Weights);
            scored.Add(new Candidate(row, score, bestQuery, authorMatch));
        }

        var winners = SelectWinners(index, scored, limit, diversity);
        var results = await HydrateAsync(conn, index, winners, queries, genreWeight, tagProfile, vocab, Idf, ct);
        logger.LogInformation(
            "Semantic reco returned {Count} of {Considered} scored candidates from {Queries} seed " +
            "quer(y/ies) in {Elapsed:F0}ms",
            results.Count, scored.Count, queries.Count, (DateTime.UtcNow - started).TotalMilliseconds);
        return results;
    }

    /// <summary>
    /// The query set: the weighted centroid, plus up to <see cref="MaxSeedQueries"/> individual
    /// seeds. The centroid alone is what dilutes a mixed library; the individual seeds alone would
    /// ignore everything past the cap, which for a 400-title library is nearly all of it. Both, so
    /// a candidate can get in either by matching the library's overall shape or by being a strong
    /// match for one title in it.
    /// </summary>
    private static List<SeedQuery> BuildQueries(
        IReadOnlyDictionary<long, float[]> seedVectors,
        IReadOnlyDictionary<long, double>? seedWeights,
        IReadOnlyDictionary<long, string> seedTitles)
    {
        var queries = new List<SeedQuery>(MaxSeedQueries + 1);

        var weighted = seedVectors
            .Select(kv => (kv.Value, Weight: seedWeights?.GetValueOrDefault(kv.Key, 1.0) ?? 1.0))
            .ToList();
        if (EmbeddingMath.WeightedMean(weighted) is { } centroid)
        {
            queries.Add(Pack(centroid, null));
        }

        foreach (var id in PickRepresentativeSeeds(seedVectors, seedWeights))
        {
            queries.Add(Pack(seedVectors[id], seedTitles.GetValueOrDefault(id)));
        }

        return queries;

        static SeedQuery Pack(float[] vector, string? title) =>
            new(EmbeddingMath.QuantizeQuery(vector, out var scale), scale, title);
    }

    /// <summary>
    /// Greedy farthest-point sampling over the seed vectors, starting from the highest-weighted
    /// seed: each next pick is the seed least similar to everything picked so far. Picking the top
    /// N by rating instead would happily spend all eight queries on eight volumes of the same
    /// series, which is exactly the dilution this is here to fix.
    /// </summary>
    private static List<long> PickRepresentativeSeeds(
        IReadOnlyDictionary<long, float[]> seedVectors, IReadOnlyDictionary<long, double>? seedWeights)
    {
        var ids = seedVectors.Keys
            .OrderByDescending(id => seedWeights?.GetValueOrDefault(id, 1.0) ?? 1.0)
            .ThenBy(id => id)
            .ToList();
        if (ids.Count <= MaxSeedQueries)
        {
            return ids;
        }

        var picked = new List<long> { ids[0] };
        var remaining = ids.Skip(1).ToList();
        var maxSimilarity = remaining
            .Select(id => (double)EmbeddingMath.Cosine(seedVectors[id], seedVectors[picked[0]]))
            .ToList();

        while (picked.Count < MaxSeedQueries && remaining.Count > 0)
        {
            var best = 0;
            for (var i = 1; i < remaining.Count; i++)
            {
                if (maxSimilarity[i] < maxSimilarity[best])
                {
                    best = i;
                }
            }

            var chosen = remaining[best];
            picked.Add(chosen);
            remaining.RemoveAt(best);
            maxSimilarity.RemoveAt(best);
            for (var i = 0; i < remaining.Count; i++)
            {
                maxSimilarity[i] = Math.Max(
                    maxSimilarity[i], EmbeddingMath.Cosine(seedVectors[remaining[i]], seedVectors[chosen]));
            }
        }

        return picked;
    }

    /// <summary>
    /// One pass over the index, cosining every surviving row against every query. Structured this
    /// way (row outer, query inner) so a row's packed bytes are read once and reused across the
    /// queries — nine queries cost far less than nine scans. A rejected row is
    /// <see cref="float.NegativeInfinity"/> in every channel, which also keeps it out of the
    /// rankings below without a second membership test.
    /// </summary>
    private static float[][] Scan(
        VectorIndex index, FilterPlan plan, List<SeedQuery> queries, HashSet<long> exclude,
        List<int[]>? requiredTagIds, CancellationToken ct)
    {
        var cosines = new float[queries.Count][];
        for (var q = 0; q < queries.Count; q++)
        {
            cosines[q] = new float[index.Count];
        }

        Parallel.For(
            0,
            index.Count,
            new ParallelOptions { CancellationToken = ct },
            row =>
            {
                var keep = index.Matches(row, plan) &&
                           !exclude.Contains(index.IdAt(row)) &&
                           (requiredTagIds is null || TagMath.ContainsAll(index.TagsAt(row), requiredTagIds));
                for (var q = 0; q < queries.Count; q++)
                {
                    cosines[q][row] = keep
                        ? index.CosineAt(row, queries[q].Packed, queries[q].Scale)
                        : float.NegativeInfinity;
                }
            });

        return cosines;
    }

    /// <summary>
    /// Reciprocal rank fusion across the per-query rankings, returning the rows that make the
    /// pool. Only membership comes out of this — the caller scores the survivors on cosines, for
    /// the reason in the class summary.
    /// </summary>
    private static List<int> FuseByRank(float[][] cosines, int poolPerQuery)
    {
        var fused = new Dictionary<int, double>();
        var survivors = new List<int>();
        for (var row = 0; row < cosines[0].Length; row++)
        {
            if (!float.IsNegativeInfinity(cosines[0][row]))
            {
                survivors.Add(row);
            }
        }

        foreach (var channel in cosines)
        {
            var ranked = survivors.ToArray();
            var keys = new float[ranked.Length];
            for (var i = 0; i < ranked.Length; i++)
            {
                keys[i] = -channel[ranked[i]]; // ascending on the negation = descending by cosine
            }

            Array.Sort(keys, ranked);
            var take = Math.Min(poolPerQuery, ranked.Length);
            for (var rank = 0; rank < take; rank++)
            {
                fused[ranked[rank]] = fused.GetValueOrDefault(ranked[rank]) + (1.0 / (RrfK + rank + 1));
            }
        }

        return [.. fused.Keys];
    }

    /// <summary>
    /// Orders the scored pool and diversifies it. Relevance is min-max normalized over the pool
    /// because MMR subtracts a cosine from it and the hybrid score has no natural range; negative
    /// candidate-to-candidate cosines are clamped away for the same reason.
    /// </summary>
    private static List<Candidate> SelectWinners(
        VectorIndex index, List<Candidate> scored, int limit, double diversity)
    {
        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        // Diversifying over the whole pool would let a wildly irrelevant outlier in on novelty
        // alone; three pages' worth is enough room to swap near-duplicates out of the first one.
        var pool = scored.Take(Math.Max(limit * 3, limit)).ToList();
        if (pool.Count == 0)
        {
            return [];
        }

        var span = pool[0].Score - pool[^1].Score;
        var relevance = pool.Select(c => span > 0 ? (c.Score - pool[^1].Score) / span : 1.0).ToList();

        var picked = EmbeddingMath.SelectDiverse(
            relevance,
            (a, b) => Math.Clamp(index.CosineBetween(pool[a].Row, pool[b].Row), 0, 1),
            limit,
            diversity);
        return picked.Select(i => pool[i]).ToList();
    }

    /// <summary>
    /// Turns winning rows into results: one dump query for the display columns the index doesn't
    /// carry, plus the matched genres/tags, which are only worth computing for the handful of rows
    /// that made it.
    /// </summary>
    private static async Task<IReadOnlyList<MangaBakaRecommendation>> HydrateAsync(
        SqliteConnection conn,
        VectorIndex index,
        List<Candidate> winners,
        List<SeedQuery> queries,
        Dictionary<string, double> genreWeight,
        TagMath.Profile tagProfile,
        IReadOnlyDictionary<int, TagInfo> vocab,
        Func<int, double> idf,
        CancellationToken ct)
    {
        if (winners.Count == 0)
        {
            return [];
        }

        var ids = winners.Select(w => index.IdAt(w.Row)).ToList();
        var rowById = winners.ToDictionary(w => index.IdAt(w.Row), w => w);

        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, title, cover_raw_url, year, description, status, rating, total_chapters, " +
            $"genres FROM dump.series WHERE id IN ({string.Join(",", ids)})";
        cmd.CommandTimeout = 600;

        var byId = new Dictionary<long, MangaBakaRecommendation>(winners.Count);
        using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetInt64(0);
                if (!rowById.TryGetValue(id, out var winner))
                {
                    continue;
                }

                var matchedGenres = ParseStringArray(GetString(reader, 8))
                    .Where(genreWeight.ContainsKey).OrderByDescending(g => genreWeight[g]).ToList();

                // Strongest shared tags for the UI — never spoilers, ranked by how much they
                // actually moved the score.
                var contributions = new List<(int Id, double Contribution)>();
                TagMath.Score(index.TagsAt(winner.Row), tagProfile, idf, contributions);
                var matchedTags = contributions
                    .OrderByDescending(m => m.Contribution)
                    .Select(m => vocab.TryGetValue(m.Id, out var info) && !info.IsSpoiler ? info.Name : null)
                    .OfType<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                byId[id] = new MangaBakaRecommendation(
                    id.ToString(CultureInfo.InvariantCulture),
                    GetString(reader, 1) ?? string.Empty,
                    GetString(reader, 2),
                    GetInt(reader, 3),
                    GetString(reader, 4),
                    MangaBakaProvider.MapStatus(GetString(reader, 5)),
                    reader.IsDBNull(6) ? index.RatingAt(winner.Row) : reader.GetDouble(6),
                    ParseCount(GetString(reader, 7)),
                    matchedGenres.Take(4).ToList(),
                    matchedTags.Take(4).ToList(),
                    winner.AuthorMatch,
                    RelationKind: null,
                    RelatedToTitle: null,
                    // "Feels like X": the individual seed whose query ranked this highest. Null
                    // when the centroid won, which is the honest answer — no one title drove it.
                    BecauseOfTitle: queries[winner.BestQuery].SeedTitle);
            }
        }

        // Preserve the ranking the selection produced; the IN query returns rows in whatever order
        // SQLite likes.
        return ids.Select(byId.GetValueOrDefault).OfType<MangaBakaRecommendation>().ToList();
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
