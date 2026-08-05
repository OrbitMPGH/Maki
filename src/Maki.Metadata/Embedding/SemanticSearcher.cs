using System.Globalization;
using System.Text.Json;
using Maki.Metadata.MangaBaka;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Maki.Metadata.Embedding;

/// <summary>
/// Natural-language catalogue search: "a quiet manga about cooking in a fantasy village" is
/// embedded with the same model that indexed every series' description, then cosined against the
/// whole in-memory index (<see cref="VectorIndex"/>).
///
/// Three channels are fused by reciprocal rank fusion — ranks add, so scores never have to be
/// calibrated against each other:
///   1. dense, the query embedding against every series vector;
///   2. lexical, the FTS5 title index, because dense search is bad at titles that are ordinary
///      words ("berserk" is a word, not a plot);
///   3. tags, the query matched against the tag vocabulary and every series scored on its own
///      tags — the only channel that can see a theme the description never states.
/// The tag channel carries a fraction of the weight of the other two, and a small popularity
/// prior breaks ties among comparable matches — see <see cref="SearchTuning"/> for every weight
/// and the measurement behind it.
/// </summary>
public class SemanticSearcher(
    EmbeddingOptions options,
    MangaBakaDumpOptions dumpOptions,
    EmbeddingStore store,
    VectorIndexCache cache,
    TextEmbedder embedder,
    MangaBakaLocalStore localStore,
    SearchTuning tuning,
    ILogger<SemanticSearcher> logger)
{
    /// <summary>
    /// bge is an asymmetric retrieval model: passages are embedded bare (as the indexer does) and
    /// queries carry this instruction. Without it, short queries land in the wrong region of the
    /// space and recall drops noticeably.
    /// </summary>
    /// Now read from the model rather than fixed here, because it is a property of the weights, not
    /// of the search: e5 wants "query: " and gte wants nothing at all, and the bge default below
    /// keeps this identical for every model shipped so far.
    private string QueryInstruction => options.Model.QueryPrefix;

    /// <summary>Enough vectors for the index to be worth searching at all.</summary>
    private const int MinIndexed = 1000;

    private readonly object _tagCacheGate = new();
    private IReadOnlyDictionary<int, float[]>? _tagVectors;
    private IReadOnlyDictionary<int, TagInfo>? _tagVocab;
    private int _tagCacheStamp = -1;

    /// <summary>True once embeddings are on and the index holds enough vectors to search.</summary>
    public bool IsReady() => options.Enabled && store.Count() >= MinIndexed;

    /// <summary>
    /// Ranked matches for a free-text query. Empty when the index isn't built — the caller falls
    /// back to title search rather than showing nothing.
    /// </summary>
    public async Task<IReadOnlyList<MangaBakaRecommendation>> SearchAsync(
        string query, RecommendationFilters? filters = null, int limit = 60, CancellationToken ct = default)
    {
        query = query?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            return [];
        }

        limit = Math.Clamp(limit, 1, 200);

        if (!await embedder.EnsureReadyAsync(ct))
        {
            logger.LogWarning("Semantic search skipped — the embedding model isn't available");
            return [];
        }

        var index = await cache.GetAsync(ct);
        if (index is null || index.Count < MinIndexed)
        {
            return [];
        }

        var plan = index.Plan(filters);
        if (plan.Impossible)
        {
            return [];
        }

        // How deep each channel ranks before the fusion. This is what a series has to reach to be
        // *considered* at all, so it is not merely a performance dial — see SearchTuning.PoolMin.
        var pool = Math.Clamp(limit * tuning.PoolMultiplier, tuning.PoolMin, tuning.PoolMax);

        var started = DateTime.UtcNow;
        var queryVector = await Task.Run(() => embedder.Embed(QueryInstruction + query), ct);
        var dense = index.Search(queryVector, plan, pool, ct);
        var lexical = await GetLexicalRanksAsync(query, ct);

        // Reciprocal rank fusion over the two rankings. A title hit that the dense pass missed
        // entirely still gets in (as long as it's indexed and passes the filters), which is what
        // makes an exact-title query work.
        var fused = new Dictionary<int, double>(dense.Count + lexical.Count);
        for (var rank = 0; rank < dense.Count; rank++)
        {
            fused[dense[rank].Row] = 1.0 / (tuning.RrfK + rank + 1);
        }

        foreach (var (id, rank) in lexical)
        {
            if (!index.TryGetRow(id, out var row) || !index.Matches(row, plan))
            {
                continue;
            }

            fused[row] = fused.GetValueOrDefault(row) + (1.0 / (tuning.RrfK + rank + 1));
        }

        // Third channel: the query is matched against the tag vocabulary, and every series in the
        // catalogue is scored on how well its own tags line up. Scored over the whole index, not
        // just the pool above — a series whose description never states its theme can only be
        // found this way, and reordering a pool it never entered would be pointless.
        foreach (var (row, rank) in RankByTagProfile(queryVector, index, plan, pool, ct))
        {
            fused[row] = fused.GetValueOrDefault(row) + (tuning.TagChannelWeight / (tuning.TagRrfK + rank + 1));
        }

        // Popularity prior, added after the channels rather than folded into one of them: it is
        // not evidence that the query means this series, it is what settles an order among rows
        // the channels already found comparable.
        if (tuning.PopularityWeight != 0)
        {
            foreach (var row in fused.Keys.ToList())
            {
                fused[row] += tuning.PopularityWeight * PopularityPrior(index.PopularityAt(row));
            }
        }

        var ranked = fused
            .OrderByDescending(kv => kv.Value)
            .ThenByDescending(kv => index.RatingAt(kv.Key))
            .Select(kv => index.IdAt(kv.Key))
            .ToList();

        var winners = ranked.Take(limit).ToList();
        var results = await HydrateAsync(winners, ct);
        logger.LogInformation(
            "Semantic search for {Length}-char query returned {Count} of {Pool} candidates in {Elapsed:F0}ms",
            query.Length, results.Count, fused.Count, (DateTime.UtcNow - started).TotalMilliseconds);
        return results;
    }

    /// <summary>
    /// A popularity rank turned into a [0,1] prior, 1 being the most popular. Log-scaled because
    /// the ranks are a long tail: the difference between rank 100 and rank 1,000 is the whole
    /// question, the difference between 60,000 and 70,000 is nothing. An unknown rank is treated
    /// as the bottom rather than the middle — the dump knows the rank of everything anyone reads.
    /// </summary>
    private double PopularityPrior(int rank)
    {
        var floor = Math.Max(2, tuning.PopularityFloorRank);
        var clamped = rank == VectorIndex.Unknown ? floor : Math.Clamp(rank, 1, floor);
        return 1.0 - (Math.Log(clamped) / Math.Log(floor));
    }

    /// <summary>
    /// Ranks the candidate pool by how well each candidate's tags match the tags the query itself
    /// resembles. Empty when the tag-name vectors haven't been built yet (an older index), or when
    /// no tag stands out enough from the rest to be worth trusting.
    /// </summary>
    private IReadOnlyList<(int Row, int Rank)> RankByTagProfile(
        float[] queryVector, VectorIndex index, FilterPlan plan, int take, CancellationToken ct)
    {
        var (tagVectors, vocab) = GetTagCache();
        if (tagVectors.Count == 0)
        {
            return [];
        }

        var matched = SelectQueryTags(queryVector, tagVectors);
        if (matched.Count == 0)
        {
            return [];
        }

        // Corpus size for the IDF: how many series are embedded, i.e. the population the tag
        // document-frequencies were counted over.
        var corpus = Math.Max(1, store.Count());
        double Idf(int tagId) =>
            vocab.TryGetValue(tagId, out var info) && info.SeriesCount > 0
                ? Math.Log((double)corpus / info.SeriesCount)
                : 1.0;

        // The query's tag profile: how much the query "wants" each tag, scaled by how
        // discriminating that tag is. Shaped like a seed profile so TagMath scores it unchanged.
        var weights = new Dictionary<int, double>(matched.Count);
        var normSq = 0.0;
        foreach (var (id, similarity) in matched)
        {
            var weight = similarity * Idf(id);
            weights[id] = weight;
            normSq += weight * weight;
        }

        var profile = new TagMath.Profile(weights, Math.Sqrt(normSq));
        if (profile.IsEmpty)
        {
            return [];
        }

        // Tag blobs live in the in-memory index, so this is a scan over packed bytes rather than
        // a keyed read per candidate.
        var scores = new double[index.Count];
        Parallel.For(
            0,
            index.Count,
            new ParallelOptions { CancellationToken = ct },
            row => scores[row] = index.Matches(row, plan)
                ? ScoreAgainstQueryTags(index.TagsAt(row), profile, Idf)
                : 0);

        var scored = new List<(int Row, double Score)>();
        for (var row = 0; row < scores.Length; row++)
        {
            if (scores[row] > 0)
            {
                scored.Add((row, scores[row]));
            }
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        logger.LogDebug(
            "Tag channel matched {Tags} tag(s) ({Names}); {Scored} series carry them",
            matched.Count,
            string.Join(", ", matched.Select(m => vocab.TryGetValue(m.Id, out var i) ? i.Name : "?")),
            scored.Count);

        return scored.Take(take).Select((x, rank) => (x.Row, rank)).ToList();
    }

    /// <summary>
    /// The tags a query is asking for: cosined against every tag name, then cut *relative to this
    /// query's own distribution* rather than at a fixed similarity.
    ///
    /// The absolute floor this replaced never fired. Query and tag name are embedded in different
    /// regimes — the query carries bge's instruction prefix and is a sentence, a tag name is two
    /// bare words — so the scale of the cosines is a property of the query's shape, not of how
    /// good the match is. Against the shipped index an instruction-prefixed query peaks near 0.42
    /// with a median of 0.19, so a 0.55 floor admitted nothing, ever; the same query embedded bare
    /// peaks at 0.97 with a median of 0.81, where 0.55 admits all 2,476 tags. What is stable
    /// across both is the *ordering* and the shape of the head: for "camping" the best tag is
    /// Camping and the next is 21% behind it, whatever the absolute numbers are. So the cut is a
    /// fraction of the best score, and separately a margin over the median so a query with no tag
    /// meaning at all doesn't admit its eight nearest neighbours off a flat distribution.
    /// </summary>
    private List<(int Id, double Similarity)> SelectQueryTags(
        float[] queryVector, IReadOnlyDictionary<int, float[]> tagVectors)
    {
        var sims = new List<(int Id, double Similarity)>(tagVectors.Count);
        foreach (var (id, vector) in tagVectors)
        {
            sims.Add((id, EmbeddingMath.Cosine(queryVector, vector)));
        }

        sims.Sort((a, b) => b.Similarity.CompareTo(a.Similarity));
        var best = sims[0].Similarity;
        var median = sims[sims.Count / 2].Similarity;

        // A negative best would make the relative test invert (a fraction of a negative number is
        // larger than it), and means no tag resembles the query at all.
        if (best <= 0)
        {
            return [];
        }

        var floor = Math.Max(
            tuning.TagFloorAbsolute,
            Math.Max(best * tuning.TagFloorRelative, median + tuning.TagFloorMedianGap));

        var matched = new List<(int Id, double Similarity)>(tuning.MaxQueryTags);
        foreach (var candidate in sims)
        {
            if (candidate.Similarity < floor || matched.Count >= tuning.MaxQueryTags)
            {
                break;
            }

            matched.Add(candidate);
        }

        return matched;
    }

    /// <summary>
    /// How well a candidate's tags satisfy the query's tag profile, in [0,1].
    /// <see cref="TagMath.Score"/> is a true cosine and divides by the candidate's own tag-vector
    /// norm, which is right when comparing two series but wrong here: it means a series carrying
    /// 200 tags scores far below one carrying five, even when both match every tag the query
    /// asked for. That systematically buries exactly the well-documented classics a search is
    /// most often looking for (Berserk has 203 tags, Attack on Titan 191). So this normalizes by
    /// the profile alone — "how much of what the query wanted is present", not "how much of this
    /// series is what the query wanted".
    /// </summary>
    private static double ScoreAgainstQueryTags(byte[]? candidateBlob, TagMath.Profile profile, Func<int, double> idf)
    {
        if (candidateBlob is null || profile.IsEmpty)
        {
            return 0;
        }

        var dot = 0.0;
        foreach (var (id, cls) in TagMath.Unpack(candidateBlob))
        {
            if (profile.IdfWeight.TryGetValue(id, out var wanted))
            {
                dot += wanted * TagMath.ClassWeight(cls) * idf(id);
            }
        }

        return dot <= 0 ? 0 : dot / (profile.Norm * profile.Norm);
    }

    /// <summary>
    /// Tag vectors and vocabulary, cached per process. Both are rewritten only by an indexing
    /// pass, so the stored row count is enough of a stamp to notice a rebuild or a downloaded
    /// index landing underneath us.
    /// </summary>
    private (IReadOnlyDictionary<int, float[]> Vectors, IReadOnlyDictionary<int, TagInfo> Vocab) GetTagCache()
    {
        var stamp = store.Count();
        lock (_tagCacheGate)
        {
            if (_tagCacheStamp == stamp && _tagVectors is not null && _tagVocab is not null)
            {
                return (_tagVectors, _tagVocab);
            }

            _tagVectors = store.GetTagVectors();
            _tagVocab = store.GetVocab();
            _tagCacheStamp = stamp;
            return (_tagVectors, _tagVocab);
        }
    }

    /// <summary>MangaBaka id → its 0-based rank in the FTS5 title index (empty when nothing matches).</summary>
    private async Task<IReadOnlyList<(long Id, int Rank)>> GetLexicalRanksAsync(string query, CancellationToken ct)
    {
        try
        {
            // Discover's ceiling is applied when the index is built, not per query: pornographic
            // entries are never embedded, and the vector side has no rating column to filter on, so
            // narrowing only the lexical channel would make the two halves of the fusion disagree.
            // ContentRating.Default keeps this exactly where it has always sat.
            var hits = await localStore.SearchAsync(query, ContentRating.Default, ct);
            return hits
                .Select((hit, rank) => (
                    Ok: long.TryParse(hit.ProviderId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id),
                    Id: id,
                    Rank: rank))
                .Where(x => x.Ok)
                .Select(x => (x.Id, x.Rank))
                .ToList();
        }
        catch (SqliteException ex)
        {
            // A long natural-language query is mostly noise to FTS5 and can fail to parse; the
            // dense ranking alone is still a good answer.
            logger.LogDebug(ex, "Lexical side of the search failed; using the dense ranking alone");
            return [];
        }
    }

    /// <summary>Reads the display columns for the winners, preserving the ranked order.</summary>
    private async Task<IReadOnlyList<MangaBakaRecommendation>> HydrateAsync(
        IReadOnlyList<long> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        using var conn = new SqliteConnection($"Data Source={dumpOptions.DatabasePath};Mode=ReadOnly;Pooling=False");
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, title, cover_raw_url, year, status, rating, total_chapters, genres, description,
                   cover_x250_x1, cover_x250_x2
            FROM series
            WHERE id IN ({string.Join(",", ids)})
            """;

        var byId = new Dictionary<long, MangaBakaRecommendation>(ids.Count);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetInt64(0);
            byId[id] = new MangaBakaRecommendation(
                id.ToString(CultureInfo.InvariantCulture),
                GetString(reader, 1) ?? string.Empty,
                GetString(reader, 2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                GetString(reader, 8),
                MangaBakaProvider.MapStatus(GetString(reader, 4)),
                reader.IsDBNull(5) ? null : reader.GetDouble(5),
                ParseCount(GetString(reader, 6)),
                // No seed profile to match against in a search, so the card just shows what the
                // series is; the query itself is the "why".
                ParseStringArray(GetString(reader, 7)).Take(3).ToList(),
                [],
                false,
                null,
                null,
                ThumbUrl: GetString(reader, 9),
                ThumbUrlHiDpi: GetString(reader, 10));
        }

        return ids.Select(byId.GetValueOrDefault).OfType<MangaBakaRecommendation>().ToList();
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

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var frac)
            ? (int)frac
            : null;
    }

    private static string? GetString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
