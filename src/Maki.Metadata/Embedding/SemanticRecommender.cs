using System.Globalization;
using System.Text.Json;
using Maki.Core.Entities;
using Maki.Metadata.MangaBaka;
using Maki.Metadata.CoRead;
using Maki.Metadata.RecoGraph;
using Maki.Metadata.Taste;
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
/// <para>
/// Which query's cosine that is comes from <see cref="RecommenderTuning.QueryAttribution"/>. The
/// shipped answer is the largest, which is not the same as the most informative: the centroid's
/// cosines sit on a higher distribution than any single seed's for the reason set out on
/// <see cref="QueryAttribution.RawCosine"/>, so it takes the maximum almost every time and the
/// per-seed queries end up deciding pool membership and nothing else.
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
    RecoGraphCache graphCache,
    RecoGraphTuning graphTuning,
    CoReadCache coReadCache,
    CoReadTuning coReadTuning,
    ILogger<SemanticRecommender> logger,
    RecommenderTuning? tuning = null,
    TasteVectorTuning? tasteTuning = null)
{
    private static readonly EmbeddingMath.Weights Weights = new();

    /// <summary>
    /// Optional at the end of the constructor rather than required, so the eval harness and the
    /// tests can sweep it without every existing call site having to name the shipped default.
    /// </summary>
    private readonly RecommenderTuning _tuning = tuning ?? RecommenderTuning.Default;

    /// <summary>Optional for the same reason <see cref="_tuning"/> is.</summary>
    private readonly TasteVectorTuning _tasteTuning = tasteTuning ?? TasteVectorTuning.Default;

    /// <summary>Standard RRF damping, same constant the search fusion uses.</summary>
    private const double RrfK = 60;

    private long _maxPopularity; // cached global popularity rank ceiling (0 = not computed)
    private long _activeCount; // cached count of active dump series, the N in idf = log(N/df)

    /// <summary>
    /// True once embeddings are on and enough vectors exist to recommend from.
    /// <para>
    /// Virtual, along with <see cref="GetSimilarAsync"/>, so a caller that wraps this in a cache can
    /// have its caching tested without standing up a dump and a vector index to answer questions the
    /// test isn't asking.
    /// </para>
    /// </summary>
    public virtual bool IsReady() => options.Enabled && store.Count() >= 1000;

    /// <summary>One query vector, packed for the integer dot path. <see cref="SeedTitle"/> is null for the centroid.</summary>
    /// <summary>
    /// One query vector, packed for the integer dot path. <see cref="SeedTitle"/> is null for the
    /// centroid, and also for a seed the dump has no title for, which is why
    /// <see cref="IsCentroid"/> is carried separately rather than inferred from it: the attribution
    /// margin compares the best seed against the centroid specifically, and a titleless seed
    /// standing in for the centroid there would silently change what the margin means.
    /// </summary>
    private sealed record SeedQuery(sbyte[] Packed, float Scale, string? SeedTitle, bool IsCentroid);

    /// <summary>A scored candidate, carrying what hydration would otherwise have to recompute.</summary>
    /// <summary>
    /// <paramref name="BestSeedQuery"/> is the seed query that explains this candidate best, or -1
    /// when no seed query ran at all, and <paramref name="Distinctiveness"/> is how much better it
    /// explains it than the centroid does. Whether that earns the right to name the seed is not
    /// decided here: under <see cref="AttributionScale.PoolRelative"/> the bar depends on the rest
    /// of the pool, which is not known until every candidate has been scored.
    /// </summary>
    private sealed record Candidate(
        int Row, double Score, int BestSeedQuery, double Distinctiveness, bool AuthorMatch,
        bool CoRecommended, bool CoRead, bool TasteMatch);

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
    /// <param name="weights">
    /// Overrides the channel weights <see cref="EmbeddingMath.HybridScore"/> combines. Null keeps the
    /// tuned defaults, which is what every caller in the app now passes.
    /// <para>
    /// It used to be load-bearing: the genre channel was a raw sum, so its scale depended on how
    /// concentrated the seed set was, and a single-seed caller had to hand in a reduced Genre weight
    /// or watch genre outrank feel. <see cref="GenreScore"/> is a cosine now and every channel here
    /// is scale-invariant in the seed count, so one weight vector serves one seed and four hundred.
    /// The parameter is kept for the eval harness, which sweeps these coefficients, and for a future
    /// caller with a real reason — <b>not</b> as a place to paper over a calibration bug.
    /// </para>
    /// </param>
    /// <param name="coGraph">
    /// Whether the co-recommendation channel may contribute. False reproduces the pre-channel
    /// behaviour exactly, which is what the instance-wide setting switches and what the eval
    /// harness needs for a baseline.
    /// </param>
    /// <param name="coRead">
    /// Whether the co-read channel may contribute. Separate from <paramref name="coGraph"/> rather
    /// than one "crowd signals" switch: the two are different artifacts with different failure
    /// modes, published and installed independently, and an install can easily have one and not the
    /// other. A single flag would make "turn off the noisy one" impossible to express.
    /// </param>
    public virtual async Task<IReadOnlyList<MangaBakaRecommendation>> GetSimilarAsync(
        IReadOnlyCollection<long> seedIds, IReadOnlyCollection<long> excludeIds,
        int limit, RecommendationFilters? filters = null, double obscurity = 0,
        IReadOnlyDictionary<long, double>? seedWeights = null, double diversity = 0,
        EmbeddingMath.Weights? weights = null, bool coGraph = true, bool coRead = true,
        bool taste = true, CancellationToken ct = default)
    {
        filters ??= RecommendationFilters.None;
        var w = weights ?? Weights;
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

        // Co-recommendation evidence, keyed by index row so the scan and the scoring loop can both
        // read it without a dictionary hop through ids. Seeded from `seedIds` rather than
        // `seedVectors.Keys`: a seed with no vector yet can still carry graph edges, and dropping it
        // would silently narrow the channel on a freshly added library.
        var graphByRow = coGraph && graphTuning.Weight > 0
            ? await BuildGraphScoresAsync(index, seedIds, seedWeights, ct)
            : [];

        var coReadByRow = coRead && coReadTuning.Weight > 0
            ? await BuildCoReadScoresAsync(index, seedIds, seedWeights, ct)
            : [];

        // Each weight is set only when its graph actually returned something. A channel whose
        // artifact is absent must not shift the score of every candidate by a constant zero term
        // it would otherwise be carrying.
        if (graphByRow.Count > 0)
        {
            w = w with { Graph = graphTuning.Weight };
        }

        if (coReadByRow.Count > 0)
        {
            w = w with { CoRead = coReadTuning.Weight };
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
        var (genreWeight, authors) = await BuildProfileAsync(conn, seedIds, seedWeights, ct);
        var genreWeightById = new Dictionary<int, double>(genreWeight.Count);
        foreach (var (name, weight) in genreWeight)
        {
            if (index.TryGetGenreId(name, out var id))
            {
                genreWeightById[id] = genreWeightById.GetValueOrDefault(id) + weight;
            }
        }

        // The profile's own magnitude, so the genre channel can be a cosine rather than a sum. See
        // GenreScore: this is the divisor that makes one seed and four hundred comparable.
        var genreNorm = Math.Sqrt(genreWeightById.Values.Sum(w => w * w));

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
        // Weighted by the same seed weights the centroid is built from — see the overload's remarks
        // for why the tag channel of all of them must not stay a flat mean.
        double CategoryWeight(int tagId) => TagMath.CategoryWeight(
            vocab.TryGetValue(tagId, out var info) ? info.Category : null,
            _tuning.TagStoryCategoryBoost);
        // Built once per request from the vocabulary, not per candidate. Empty (and free) whenever
        // the decay is 0 or the index predates the name_path column, in which case every tag scores
        // exactly as it did before this existed.
        var tagTree = TagMath.TagTree.Build(
            vocab, _tuning.TagAncestorDecay, _tuning.TagAncestorIncludesSelf, activeCount);
        var tagProfile = TagMath.BuildProfile(
            [.. store.GetTagBlobs(seedIds)
                .Select(kv => (kv.Value, seedWeights?.GetValueOrDefault(kv.Key, 1.0) ?? 1.0))],
            Idf,
            _tuning.TagProfileSharpening,
            CategoryWeight,
            _tuning.TagConsensusPower,
            tagTree);

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
        var queries = BuildQueries(seedVectors, seedWeights, seedTitles, _tuning);
        if (queries.Count == 0)
        {
            return [];
        }

        // Live only when an artifact is actually loaded and the weight is positive, exactly like the
        // two crowd channels: a missing file leaves every result byte-identical to before this
        // channel existed.
        var tasteLive = taste && index.Taste is not null && _tasteTuning.Weight > 0;
        if (tasteLive)
        {
            w = w with { Taste = _tasteTuning.Weight };
        }

        var exclude = new HashSet<long>(seedIds.Concat(excludeIds));
        exclude.UnionWith(await GetDuplicateIdsAsync(conn, seedTitles, exclude, ct));
        // popularity_global_current is a global rank (1 = most popular). Normalize to a percentile
        // for the obscurity term; only needed when the dial is off-centre.
        var maxPopularity = obscurity != 0 ? await GetMaxPopularityAsync(conn, ct) : 1;
        var logMaxPopularity = Math.Log(Math.Max(2, maxPopularity));

        // Behavioural queries, built from whichever seeds the artifact actually covers. A seed can
        // have a text vector and no behavioural one (nobody on AniList listed it) or the reverse, so
        // the two sets are assembled independently rather than one being filtered by the other.
        var tasteQueries = tasteLive ? BuildTasteQueries(index, seedIds, seedWeights, _tasteTuning) : [];

        var started = DateTime.UtcNow;
        var (cosines, tasteCosines) = Scan(index, plan, queries, tasteQueries, exclude, requiredTagIds, ct);
        // Collapsed to one number per row before anything reads it: the behavioural channel has no
        // attribution to do, so unlike the text channels there is nothing to gain from keeping the
        // per-query breakdown alive through scoring.
        var tasteByRow = BestPerRow(tasteCosines, index.Count);
        var pooled = FuseByRank(cosines, Math.Clamp(limit * 4, 200, 2000));
        // Injected separately and capped separately. Merging the two score maps first would let
        // the denser co-read graph spend the vote graph's budget, and the caps are the dial that
        // actually controls each channel's intensity (see RecoGraphTuning.MaxInjected).
        var injected = InjectGraphCandidates(cosines, pooled, graphByRow, graphTuning);
        var coReadInjected = InjectCoReadCandidates(
            cosines, pooled, injected, coReadByRow, coReadTuning);
        // The coverage win and the risk in one. The artifact reaches rows no text query would rank,
        // which is the whole point, but pool entry lets a row be ranked on genre, tag, author and
        // quality too - so it is gated on corroboration exactly like the two crowd channels.
        var tasteInjected = InjectTasteCandidates(
            cosines, pooled, injected.Concat(coReadInjected), tasteByRow, _tasteTuning);

        // Which rows got here on crowd evidence rather than on cosine. Only used to decide whether
        // the cosine floor may drop them, and only when the tuning says so.
        var crowdInjected = _tuning.CrowdBypassesCosineFloor
            ? new HashSet<int>(injected.Concat(coReadInjected).Concat(tasteInjected))
            : [];

        // Per-query mean and spread, so the loop below can ask which query finds a row unusually
        // similar rather than which query is scaled highest. Null in RawCosine mode, where nothing
        // reads it and the pass is not worth paying for.
        var scales = _tuning.QueryAttribution == QueryAttribution.RawCosine
            ? null
            : MeasureQueries(cosines);

        var scored = new List<Candidate>(
            pooled.Count + injected.Count + coReadInjected.Count + tasteInjected.Count);
        var floored = 0;
        foreach (var row in pooled.Concat(injected).Concat(coReadInjected).Concat(tasteInjected))
        {
            var bestCosine = double.NegativeInfinity;
            var creditQuery = 0;
            var bestCredit = double.NegativeInfinity;
            var centroidCredit = double.NegativeInfinity;
            var bestSeedCredit = double.NegativeInfinity;
            var bestSeedQuery = -1;
            for (var q = 0; q < queries.Count; q++)
            {
                var cosine = cosines[q][row];
                if (cosine > bestCosine)
                {
                    bestCosine = cosine;
                }

                // Credit is the raw cosine unless the channels were measured, in which case it is
                // how far above that channel's own mean this row sits. A channel with no spread
                // says nothing about any row, so it credits zero rather than dividing by nearly
                // nothing.
                var credit = scales is null
                    ? cosine
                    : scales[q].Deviation <= 0
                        ? 0
                        : (cosine - scales[q].Mean) / scales[q].Deviation;

                if (credit > bestCredit)
                {
                    bestCredit = credit;
                    creditQuery = q;
                }

                if (queries[q].IsCentroid)
                {
                    centroidCredit = credit;
                }
                else if (credit > bestSeedCredit)
                {
                    bestSeedCredit = credit;
                    bestSeedQuery = q;
                }
            }

            // How much better the best single seed explains this row than the library as a whole
            // does. Zero when there is no seed query (a single-seed request is centroid only) or no
            // centroid to measure against, both of which mean there is nothing to claim.
            var distinctiveness = bestSeedQuery >= 0 && !double.IsNegativeInfinity(centroidCredit)
                ? bestSeedCredit - centroidCredit
                : 0;

            if (scales is not null && _tuning.QueryAttribution == QueryAttribution.Standardized)
            {
                // No longer a maximum, so this is systematically below what RawCosine would have
                // scored and the floor below rejects more rows. That is the trade, not a
                // regression - see QueryAttribution.Standardized.
                bestCosine = cosines[creditQuery][row];
            }

            var graphScore = graphByRow.GetValueOrDefault(row);
            var coReadScore = coReadByRow.GetValueOrDefault(row);
            // Floored at 0 in BestPerRow, so a row the artifact does not cover and a row it covers
            // and finds dissimilar are both worth nothing here rather than one being a penalty.
            var tasteScore = tasteByRow.Length > row && !float.IsNegativeInfinity(tasteByRow[row])
                ? tasteByRow[row]
                : 0;
            if (bestCosine < _tuning.CosineFloor && !crowdInjected.Contains(row))
            {
                // Counted, not just skipped: a row the crowd channels paid a MaxInjected slot for
                // and that dies here is the channel being capped by a gate nothing measured, which
                // is invisible without a number for it.
                if (graphScore > 0 || coReadScore > 0)
                {
                    floored++;
                }

                continue;
            }

            var genreSum = GenreScore(
                index.GenresAt(row), genreWeightById, _tuning.GenreChannelIsRawSum ? 0 : genreNorm);

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
                TagMath.Score(
                    index.TagsAt(row), tagProfile, Idf, null, _tuning.TagCandidateNormPower,
                    CategoryWeight, tagTree),
                authorMatch,
                index.RatingAt(row),
                obscurity,
                percentile,
                w,
                graphScore,
                coReadScore,
                Math.Max(0, distinctiveness),
                tasteScore);
            scored.Add(new Candidate(
                row, score, bestSeedQuery, distinctiveness, authorMatch,
                graphScore > 0, coReadScore > 0, tasteScore > 0));
        }

        var winners = SelectWinners(index, scored, limit, diversity, seedIds, _tuning);
        // Calibrated against the winners, not the scored pool. The pool is RRF-fused, so it holds
        // the top slice of every query at once - by construction the most seed-specific rows in the
        // catalogue, and nothing like what comes back. Measured on a 92-seed library the pool ran to
        // 9,300 rows with a mean distinctiveness of 1.62 against 0.69 among the 40 actually
        // returned, so calibrating on it set the bar at 1.95 where the page warranted 0.81 and a
        // near-duplicate of a seed scoring 1.15 went unnamed.
        var cutoff = AttributionCutoff([.. winners.Select(c => c.Distinctiveness)], _tuning);
        var results = await HydrateAsync(
            conn, index, winners, queries, genreWeight, tagProfile, vocab, Idf, CategoryWeight, tagTree,
            cutoff, ct);
        logger.LogInformation(
            "Semantic reco returned {Count} of {Considered} scored candidates from {Queries} seed " +
            "quer(y/ies) in {Elapsed:F0}ms ({Injected} co-recommended and {CoReadInjected} co-read " +
            "candidates joined the pool, {Floored} crowd-backed rows dropped by the cosine floor)",
            results.Count, scored.Count, queries.Count, (DateTime.UtcNow - started).TotalMilliseconds,
            injected.Count, coReadInjected.Count, floored);
        return results;
    }

    /// <summary>
    /// Cosine ∈ [0,1] between the seed genre profile and a candidate's genres, deliberately the same
    /// shape as <see cref="TagMath.Score"/>.
    ///
    /// <para>
    /// It used to be a plain sum of the matched profile weights, and that made the channel's scale
    /// depend on how CONCENTRATED the seed set is rather than on how well a candidate matched.
    /// <see cref="BuildProfileAsync"/> gives each seed's genre <c>1/seedCount</c>, so a genre every
    /// seed carries scores 1.0 whether that is two seeds or four hundred: a narrow seed set handed a
    /// three-genre candidate ~3.0, against a semantic term that tops out at 3.0 × cosine. Genre then
    /// outranked feel on exactly the requests where feel is all there is — the "More like this" rail
    /// and a two-or-three-seed Discover.
    /// </para>
    ///
    /// <para>
    /// Measured on 500 single-seed and 400 three-seed requests graded against the held-out vote
    /// graph (<c>distribution/eval-reco-labels.cs</c>): simply lowering the coefficient to 0.15 took
    /// nDCG@40 from 0.115 to 0.132 at one seed and 0.071 to 0.099 at three, with median pick
    /// popularity flat, so it was not winning by returning famous titles. Normalizing gets the same
    /// correction without a magic number per caller, which is what let
    /// <c>SimilarSeriesService</c>'s single-seed weight override be deleted.
    /// </para>
    /// </summary>
    /// <param name="profileNorm">
    /// The profile's magnitude, or 0 to return the raw sum instead — which is what
    /// <see cref="RecommenderTuning.GenreChannelIsRawSum"/> asks for and nothing in the app does.
    /// </param>
    private static double GenreScore(
        ReadOnlySpan<int> candidateGenres, Dictionary<int, double> profile, double profileNorm)
    {
        if (candidateGenres.Length == 0)
        {
            return 0;
        }

        var dot = 0.0;
        foreach (var g in candidateGenres)
        {
            dot += profile.GetValueOrDefault(g);
        }

        if (dot <= 0)
        {
            return 0;
        }

        // The candidate side is a binary vector, so its norm is just the square root of how many
        // genres it carries. Dividing by it is what stops a title tagged with a dozen genres
        // collecting a match against every profile it is shown.
        return profileNorm <= 0 ? dot : dot / (profileNorm * Math.Sqrt(candidateGenres.Length));
    }

    /// <summary>
    /// The query set: the weighted centroid, plus up to <see cref="RecommenderTuning.MaxSeedQueries"/> individual
    /// seeds. The centroid alone is what dilutes a mixed library; the individual seeds alone would
    /// ignore everything past the cap, which for a 400-title library is nearly all of it. Both, so
    /// a candidate can get in either by matching the library's overall shape or by being a strong
    /// match for one title in it.
    /// <para>
    /// One seed is the exception: the centroid of a single vector <em>is</em> that vector, so the
    /// per-seed query would be a byte-identical duplicate that doubles <see cref="Scan"/> for
    /// nothing. Dropping it changes no result — the tie already resolved to the centroid, whose
    /// <see cref="SeedQuery.SeedTitle"/> is null, and "feels like" the one series you are already
    /// looking at is not an explanation worth printing.
    /// </para>
    /// </summary>
    private static List<SeedQuery> BuildQueries(
        IReadOnlyDictionary<long, float[]> seedVectors,
        IReadOnlyDictionary<long, double>? seedWeights,
        IReadOnlyDictionary<long, string> seedTitles,
        RecommenderTuning tuning)
    {
        var queries = new List<SeedQuery>(tuning.MaxSeedQueries + 1);

        var weighted = seedVectors
            .Select(kv => (kv.Value, Weight: seedWeights?.GetValueOrDefault(kv.Key, 1.0) ?? 1.0))
            .ToList();
        if (EmbeddingMath.WeightedMean(weighted) is { } centroid)
        {
            queries.Add(Pack(centroid, null, isCentroid: true));
            if (seedVectors.Count == 1)
            {
                return queries;
            }
        }

        foreach (var id in PickRepresentativeSeeds(seedVectors, seedWeights, tuning))
        {
            queries.Add(Pack(seedVectors[id], seedTitles.GetValueOrDefault(id)));
        }

        return queries;

        static SeedQuery Pack(float[] vector, string? title, bool isCentroid = false) =>
            new(EmbeddingMath.QuantizeQuery(vector, out var scale), scale, title, isCentroid);
    }

    /// <summary>
    /// Which seeds get their own query once there are more of them than
    /// <see cref="RecommenderTuning.MaxSeedQueries"/>. Below that every seed is queried and the
    /// strategy cannot matter, which is why this only moves anything on a whole-library request.
    /// <para>
    /// Seeds are ordered by weight first in every strategy, so the tie-break behaviour
    /// <c>TasteVectorTuning.WeightQuantum</c>'s remarks describe still applies.
    /// </para>
    /// </summary>
    internal static List<long> PickRepresentativeSeeds(
        IReadOnlyDictionary<long, float[]> seedVectors,
        IReadOnlyDictionary<long, double>? seedWeights,
        RecommenderTuning tuning)
    {
        var ids = seedVectors.Keys
            .OrderByDescending(id => seedWeights?.GetValueOrDefault(id, 1.0) ?? 1.0)
            .ThenBy(id => id)
            .ToList();
        if (ids.Count <= tuning.MaxSeedQueries)
        {
            return ids;
        }

        double Weight(long id) => Math.Max(0.01, seedWeights?.GetValueOrDefault(id, 1.0) ?? 1.0);

        return tuning.SeedSelection switch
        {
            SeedSelection.Weight => ids.Take(tuning.MaxSeedQueries).ToList(),
            SeedSelection.Medoid => PickMedoids(seedVectors, ids, Weight, tuning.MaxSeedQueries),
            SeedSelection.WeightedFarthest => PickFarthest(seedVectors, ids, Weight, tuning.MaxSeedQueries),
            _ => PickFarthest(seedVectors, ids, _ => 1.0, tuning.MaxSeedQueries),
        };
    }

    /// <summary>
    /// Greedy farthest-point sampling from the highest-weighted seed: each next pick maximizes
    /// <c>(1 - similarity to everything picked) * weight</c>. With a constant weight that is plain
    /// farthest-point sampling, which is what shipped; with the real weights it is
    /// <see cref="SeedSelection.WeightedFarthest"/>, and the difference is whether taste steers the
    /// whole walk or only where it starts.
    /// <para>
    /// Picking the top N by weight instead would happily spend every query on volumes of one series,
    /// which is the dilution this exists to fix — but it also means the picks after the first are
    /// the seed set's corners, not its centres, hence <see cref="PickMedoids"/> as the alternative.
    /// </para>
    /// </summary>
    private static List<long> PickFarthest(
        IReadOnlyDictionary<long, float[]> seedVectors,
        List<long> ids,
        Func<long, double> weight,
        int take)
    {
        var picked = new List<long> { ids[0] };
        var remaining = ids.Skip(1).ToList();
        var maxSimilarity = remaining
            .Select(id => (double)EmbeddingMath.Cosine(seedVectors[id], seedVectors[picked[0]]))
            .ToList();

        while (picked.Count < take && remaining.Count > 0)
        {
            var best = 0;
            var bestScore = double.NegativeInfinity;
            for (var i = 0; i < remaining.Count; i++)
            {
                var score = (1.0 - maxSimilarity[i]) * weight(remaining[i]);
                if (score > bestScore)
                {
                    bestScore = score;
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
    /// Weighted k-means over the seed vectors, then the seed nearest each cluster's mean. Where
    /// farthest-point sampling returns the seed set's hull, this returns one seed per region the
    /// library actually occupies, sized by how much of the library sits there — so a reader with
    /// forty romance titles and one outlier spends its queries on the romance rather than on the
    /// outlier.
    /// <para>
    /// Deterministic on purpose: the initial centres come from the farthest-point walk rather than
    /// from a random k-means++ draw, so two identical requests cannot return different pools and the
    /// 12-hour cache key stays honest. Vectors are unit length, so cosine and Euclidean order
    /// identically and the means only need renormalizing.
    /// </para>
    /// </summary>
    private static List<long> PickMedoids(
        IReadOnlyDictionary<long, float[]> seedVectors,
        List<long> ids,
        Func<long, double> weight,
        int take)
    {
        const int Iterations = 10;

        var centres = PickFarthest(seedVectors, ids, _ => 1.0, take)
            .Select(id => (float[])seedVectors[id].Clone())
            .ToList();
        var assignment = new int[ids.Count];

        for (var pass = 0; pass < Iterations; pass++)
        {
            var moved = false;
            for (var i = 0; i < ids.Count; i++)
            {
                var best = 0;
                var bestSimilarity = float.NegativeInfinity;
                for (var c = 0; c < centres.Count; c++)
                {
                    var similarity = EmbeddingMath.Cosine(seedVectors[ids[i]], centres[c]);
                    if (similarity > bestSimilarity)
                    {
                        bestSimilarity = similarity;
                        best = c;
                    }
                }

                if (assignment[i] != best)
                {
                    assignment[i] = best;
                    moved = true;
                }
            }

            if (pass > 0 && !moved)
            {
                break;
            }

            for (var c = 0; c < centres.Count; c++)
            {
                var members = new List<(float[] Vector, double Weight)>();
                for (var i = 0; i < ids.Count; i++)
                {
                    if (assignment[i] == c)
                    {
                        members.Add((seedVectors[ids[i]], weight(ids[i])));
                    }
                }

                // An empty cluster keeps its previous centre rather than being dropped: losing it
                // would silently return fewer queries than asked for, and the caller sizes its scan
                // on the count it gets back.
                if (EmbeddingMath.WeightedMean(members) is { } mean)
                {
                    centres[c] = mean;
                }
            }
        }

        var picked = new List<long>(take);
        var used = new HashSet<long>();
        for (var c = 0; c < centres.Count; c++)
        {
            var best = -1L;
            var bestSimilarity = float.NegativeInfinity;
            for (var i = 0; i < ids.Count; i++)
            {
                if (assignment[i] != c || used.Contains(ids[i]))
                {
                    continue;
                }

                var similarity = EmbeddingMath.Cosine(seedVectors[ids[i]], centres[c]);
                if (similarity > bestSimilarity)
                {
                    bestSimilarity = similarity;
                    best = ids[i];
                }
            }

            if (best >= 0)
            {
                picked.Add(best);
                used.Add(best);
            }
        }

        // Clusters that came out empty leave fewer picks than asked for; backfill by weight so the
        // request still issues the number of queries it is paying for.
        foreach (var id in ids)
        {
            if (picked.Count >= take)
            {
                break;
            }

            if (used.Add(id))
            {
                picked.Add(id);
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
    /// <summary>
    /// One pass over every row, answering both spaces. The filter predicate is the expensive part
    /// and it is identical for both, so scanning twice would pay for it twice; the behavioural
    /// vectors are also a fraction of the text vectors&apos; width, which is what makes the second
    /// space close to free.
    /// </summary>
    private static (float[][] Text, float[][] Taste) Scan(
        VectorIndex index, FilterPlan plan, List<SeedQuery> queries, List<SeedQuery> tasteQueries,
        HashSet<long> exclude, List<int[]>? requiredTagIds, CancellationToken ct)
    {
        var cosines = new float[queries.Count][];
        for (var q = 0; q < queries.Count; q++)
        {
            cosines[q] = new float[index.Count];
        }

        var taste = new float[tasteQueries.Count][];
        for (var q = 0; q < tasteQueries.Count; q++)
        {
            taste[q] = new float[index.Count];
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

                for (var q = 0; q < tasteQueries.Count; q++)
                {
                    // NEGATIVE infinity for a filtered row, but plain 0 for a row the artifact has
                    // no vector for. The first can never be shown; the second simply has no
                    // behavioural evidence and must still be rankable on everything else.
                    taste[q][row] = keep
                        ? index.TasteCosineAt(row, tasteQueries[q].Packed, tasteQueries[q].Scale)
                        : float.NegativeInfinity;
                }
            });

        return (cosines, taste);
    }

    /// <summary>
    /// Keeps at most <see cref="RecommenderTuning.MaxPerFranchise"/> members of any one same-work
    /// component, and optionally drops every member of a component a seed belongs to.
    ///
    /// <para>
    /// Runs on the SORTED list, so the member kept is the best-scoring one rather than whichever the
    /// scan reached first. It runs before the pool is trimmed to <c>limit * 3</c> for MMR, so a
    /// collapsed franchise gives its slots back to other candidates instead of leaving the page
    /// short - which is the whole difference between suppressing a duplicate and losing a result.
    /// </para>
    ///
    /// <para>
    /// MMR cannot do this job. It diversifies on the embedding cosine, and two volumes of one series
    /// are not reliably near each other in that space: they are separate entries with separate
    /// descriptions, and one of them is often a summary of a story the other has not told yet.
    /// </para>
    /// </summary>
    private static List<Candidate> CollapseFranchises(
        List<Candidate> scored, VectorIndex index, IReadOnlyCollection<long> seedIds,
        RecommenderTuning tuning)
    {
        if (tuning.MaxPerFranchise <= 0 && !tuning.ExcludeSeedFranchise)
        {
            return scored;
        }

        var seedFranchises = new HashSet<int>();
        if (tuning.ExcludeSeedFranchise)
        {
            foreach (var id in seedIds)
            {
                if (index.TryGetRow(id, out var row) && index.FranchiseAt(row) != VectorIndex.Unknown)
                {
                    seedFranchises.Add(index.FranchiseAt(row));
                }
            }
        }

        var seen = new Dictionary<int, int>();
        var kept = new List<Candidate>(scored.Count);
        foreach (var candidate in scored)
        {
            var franchise = index.FranchiseAt(candidate.Row);
            // Unknown is "in no franchise", which is most of the catalogue. It is not a component,
            // and treating it as one would collapse every unrelated series into a single slot.
            if (franchise == VectorIndex.Unknown)
            {
                kept.Add(candidate);
                continue;
            }

            if (seedFranchises.Contains(franchise))
            {
                continue;
            }

            var count = seen.GetValueOrDefault(franchise);
            if (tuning.MaxPerFranchise > 0 && count >= tuning.MaxPerFranchise)
            {
                continue;
            }

            seen[franchise] = count + 1;
            kept.Add(candidate);
        }

        return kept;
    }

    /// <summary>Best score per row across a set of channels, with no evidence reading as 0.</summary>
    private static float[] BestPerRow(float[][] channels, int rows)
    {
        var best = new float[rows];
        if (channels.Length == 0)
        {
            return best;
        }

        for (var row = 0; row < rows; row++)
        {
            var top = float.NegativeInfinity;
            for (var q = 0; q < channels.Length; q++)
            {
                if (channels[q][row] > top)
                {
                    top = channels[q][row];
                }
            }

            // A filtered row keeps its sentinel so the injector can recognise it; anything else
            // floors at 0, because a negative behavioural cosine is evidence of dissimilarity and
            // must not be able to subtract from a candidate&apos;s score.
            best[row] = float.IsNegativeInfinity(top) ? float.NegativeInfinity : Math.Max(0, top);
        }

        return best;
    }

    /// <summary>
    /// Behavioural query vectors: the weighted centroid of whatever seeds the artifact covers, plus
    /// the heaviest individual seeds up to <see cref="TasteVectorTuning.MaxSeedQueries"/>.
    ///
    /// <para>
    /// No farthest-point walk here. That strategy exists in the text space to spend a small query
    /// budget on the seed set&apos;s hull, and it was measured to be worth no more than any other
    /// selection; this space is narrower and cheaper per query, so the budget buys more by simply
    /// being larger.
    /// </para>
    /// </summary>
    private static List<SeedQuery> BuildTasteQueries(
        VectorIndex index, IReadOnlyCollection<long> seedIds,
        IReadOnlyDictionary<long, double>? seedWeights, TasteVectorTuning tuning)
    {
        if (index.Taste is null)
        {
            return [];
        }

        var vectors = new List<(float[] Vec, double Weight)>();
        foreach (var id in seedIds)
        {
            if (index.TryGetRow(id, out var row) && index.TasteVectorAt(row) is { } vector)
            {
                vectors.Add((vector, seedWeights?.GetValueOrDefault(id, 1.0) ?? 1.0));
            }
        }

        if (vectors.Count == 0)
        {
            return [];
        }

        var queries = new List<SeedQuery>(tuning.MaxSeedQueries + 1);
        if (EmbeddingMath.WeightedMean(vectors) is { } centroid)
        {
            queries.Add(Pack(centroid));
            if (vectors.Count == 1)
            {
                return queries;
            }
        }

        foreach (var (vec, _) in vectors.OrderByDescending(v => v.Weight).Take(tuning.MaxSeedQueries))
        {
            queries.Add(Pack(vec));
        }

        return queries;

        static SeedQuery Pack(float[] vector) =>
            new(EmbeddingMath.QuantizeQuery(vector, out var scale), scale, null, false);
    }

    /// <summary>
    /// Rows the behavioural channel vouches for that no text query pooled. Same contract as
    /// <see cref="InjectGraphCandidates"/>: it reuses <c>Scan</c>&apos;s negative-infinity sentinel
    /// rather than re-testing the filters, so there is no second copy of
    /// <c>RecommendationFilters</c>&apos;s logic and no way to smuggle a row past one.
    /// </summary>
    internal static List<int> InjectTasteCandidates(
        float[][] cosines, List<int> pooled, IEnumerable<int> alreadyInjected, float[] tasteByRow,
        TasteVectorTuning tuning)
    {
        var injected = new List<int>();
        if (tuning.Weight <= 0 || tuning.MaxInjected <= 0 || cosines.Length == 0)
        {
            return injected;
        }

        var taken = new HashSet<int>(pooled);
        taken.UnionWith(alreadyInjected);

        var best = 0f;
        for (var row = 0; row < tasteByRow.Length; row++)
        {
            if (!float.IsNegativeInfinity(tasteByRow[row]) && tasteByRow[row] > best)
            {
                best = tasteByRow[row];
            }
        }

        if (best <= 0)
        {
            return injected;
        }

        var floor = best * tuning.MinInjectedScore;
        var candidates = new List<(int Row, float Score)>();
        for (var row = 0; row < tasteByRow.Length; row++)
        {
            var score = tasteByRow[row];
            if (float.IsNegativeInfinity(score) || score < floor || taken.Contains(row))
            {
                continue;
            }

            // The same sentinel test InjectGraphCandidates uses: channel 0 holding -inf means the
            // row failed the filter plan, the exclusion set or the required tags.
            if (float.IsNegativeInfinity(cosines[0][row]))
            {
                continue;
            }

            candidates.Add((row, score));
        }

        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        foreach (var (row, _) in candidates.Take(tuning.MaxInjected))
        {
            injected.Add(row);
        }

        return injected;
    }

    /// <summary>
    /// The distinctiveness a candidate has to exceed before it may name a seed.
    ///
    /// <para>
    /// Under <see cref="AttributionScale.Absolute"/> this is just the configured margin, which is
    /// what shipped and which does not survive a change of library size: the gap's mean climbs with
    /// the seed count while its spread stays put, so a fixed bar walks through the distribution
    /// rather than holding a position in it.
    /// </para>
    ///
    /// <para>
    /// Under <see cref="AttributionScale.PoolRelative"/> the margin is read as standard deviations
    /// of the returned candidates' distinctiveness spread, so it asks "is this candidate distinctive
    /// <em>compared with the others we are about to show</em>" - a question whose answer does not
    /// move when the library grows. The cutoff never drops below zero, so a row the centroid
    /// explains better than any single seed stays unnamed however flat the pool is; and a pool with
    /// no spread at all names nobody rather than everybody, since "nothing here stands out" is the
    /// honest reading of that.
    /// </para>
    /// </summary>
    internal static double AttributionCutoff(
        IReadOnlyList<double> distinctiveness, RecommenderTuning tuning)
    {
        if (tuning.AttributionScale == AttributionScale.Absolute)
        {
            return tuning.AttributionMargin;
        }

        if (distinctiveness.Count == 0)
        {
            return double.PositiveInfinity;
        }

        var mean = distinctiveness.Average();
        var variance = distinctiveness.Sum(d => (d - mean) * (d - mean)) / distinctiveness.Count;
        var deviation = Math.Sqrt(Math.Max(0, variance));
        return deviation <= 0
            ? double.PositiveInfinity
            : Math.Max(0, mean + (tuning.AttributionMargin * deviation));
    }

    /// <summary>
    /// One query channel's cosine distribution over the rows that survived the filter plan.
    /// <see cref="Deviation"/> is 0 when the channel has no spread at all, which only happens on a
    /// degenerate index but must not become a division.
    /// </summary>
    internal readonly record struct QueryScale(double Mean, double Deviation);

    /// <summary>
    /// Mean and standard deviation per query channel, over survivors only. Rejected rows are
    /// <see cref="float.NegativeInfinity"/> in <em>every</em> channel (see <see cref="Scan"/>), so
    /// each channel measures the same row set and the resulting z-scores are comparable across
    /// queries, which is the whole point of computing them.
    ///
    /// <para>
    /// Single pass with double accumulators: the values are cosines in [-1, 1] and the counts are in
    /// the hundreds of thousands, so the mean and the second moment stay far enough apart for
    /// <c>E[x^2] - E[x]^2</c> to hold its precision. Cost is one linear read per channel against a
    /// scan that already did a full dot product per row per channel.
    /// </para>
    /// </summary>
    internal static QueryScale[] MeasureQueries(float[][] cosines)
    {
        var scales = new QueryScale[cosines.Length];
        for (var q = 0; q < cosines.Length; q++)
        {
            var channel = cosines[q];
            var count = 0L;
            var sum = 0.0;
            var sumSquares = 0.0;
            foreach (var value in channel)
            {
                if (float.IsNegativeInfinity(value))
                {
                    continue;
                }

                count++;
                sum += value;
                sumSquares += (double)value * value;
            }

            if (count == 0)
            {
                scales[q] = new QueryScale(0, 0);
                continue;
            }

            var mean = sum / count;
            var variance = Math.Max(0, (sumSquares / count) - (mean * mean));
            scales[q] = new QueryScale(mean, Math.Sqrt(variance));
        }

        return scales;
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
    /// Co-recommendation score per index row, from the graph artifact. Empty when there is no
    /// artifact installed, which is the normal state until one is published — the channel then
    /// contributes nothing and everything downstream behaves exactly as it did before it existed.
    /// </summary>
    private async Task<Dictionary<int, double>> BuildGraphScoresAsync(
        VectorIndex index,
        IReadOnlyCollection<long> seedIds,
        IReadOnlyDictionary<long, double>? seedWeights,
        CancellationToken ct)
    {
        var graph = await graphCache.GetAsync(ct);
        if (graph is null)
        {
            return [];
        }

        var byId = RecoGraphScorer.Score(graph, seedIds, seedWeights, graphTuning);
        var byRow = new Dictionary<int, double>(byId.Count);
        foreach (var (id, score) in byId)
        {
            // The graph covers series the vector index does not (novels, inactive rows, anything
            // never embedded). Those are not recommendable, so they are dropped here rather than
            // being carried to a lookup that would fail later.
            if (index.TryGetRow(id, out var row))
            {
                byRow[row] = score;
            }
        }

        return byRow;
    }

    /// <summary>
    /// The co-read equivalent of <see cref="BuildGraphScoresAsync"/>. Kept as its own method rather
    /// than a parameterized one: the two scorers take different tunings and mean different things,
    /// and the only shared part is the id-to-row projection below, which is three lines.
    /// </summary>
    private async Task<Dictionary<int, double>> BuildCoReadScoresAsync(
        VectorIndex index,
        IReadOnlyCollection<long> seedIds,
        IReadOnlyDictionary<long, double>? seedWeights,
        CancellationToken ct)
    {
        var graph = await coReadCache.GetAsync(ct);
        if (graph is null)
        {
            return [];
        }

        var byId = CoReadScorer.Score(graph, seedIds, seedWeights, coReadTuning);
        var byRow = new Dictionary<int, double>(byId.Count);
        foreach (var (id, score) in byId)
        {
            // The graph covers series the vector index does not (novels, inactive rows, anything
            // never embedded). Those are not recommendable, so they are dropped here rather than
            // being carried to a lookup that would fail later.
            if (index.TryGetRow(id, out var row))
            {
                byRow[row] = score;
            }
        }

        return byRow;
    }

    /// <summary>
    /// Rows the co-read graph vouches for that nothing else pooled, best-scoring first and capped.
    /// Same contract as <see cref="InjectGraphCandidates"/>, and it excludes that method's output
    /// too: a row both channels vouch for is already in, and letting it consume a slot here would
    /// silently shrink this channel's real cap.
    /// </summary>
    internal static List<int> InjectCoReadCandidates(
        float[][] cosines,
        List<int> pooled,
        List<int> alreadyInjected,
        Dictionary<int, double> coReadByRow,
        CoReadTuning tuning)
    {
        if (coReadByRow.Count == 0 || tuning.MaxInjected <= 0)
        {
            return [];
        }

        var already = new HashSet<int>(pooled);
        already.UnionWith(alreadyInjected);

        var candidates = new List<(int Row, double Score)>();
        foreach (var (row, score) in coReadByRow)
        {
            if (score >= tuning.MinInjectedScore
                && !already.Contains(row)
                && !float.IsNegativeInfinity(cosines[0][row]))
            {
                candidates.Add((row, score));
            }
        }

        if (candidates.Count > tuning.MaxInjected)
        {
            candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
            candidates.RemoveRange(tuning.MaxInjected, candidates.Count - tuning.MaxInjected);
        }

        return [.. candidates.Select(c => c.Row)];
    }

    /// <summary>
    /// Rows the graph vouches for that no channel's ranking pooled, best-scoring first and capped.
    /// This is what lets the channel <em>discover</em> rather than merely reorder: a candidate the
    /// embeddings rank 40,000th is never considered, however many readers pair it with the library.
    ///
    /// <para>
    /// Only well-corroborated candidates get in, per
    /// <see cref="RecoGraphTuning.MinInjectedScore"/>. Pool entry matters more than it looks:
    /// <see cref="FuseByRank"/> pools on cosine alone while
    /// <see cref="EmbeddingMath.HybridScore"/> ranks on the structured channels too, so a row that
    /// enters here can win on genre, tag and quality without ever having been near the cosine
    /// top-200. That is the point when several seeds agree, and a bug when it rests on one edge.
    /// </para>
    ///
    /// <para>
    /// Filter survival is already decided — <see cref="Scan"/> writes
    /// <see cref="float.NegativeInfinity"/> into every channel for a row that failed the filter
    /// plan, the exclusion set, or the required tags — so testing channel 0 for that sentinel is the
    /// same predicate <see cref="FuseByRank"/> uses, and no filter logic is duplicated here.
    /// </para>
    /// </summary>
    internal static List<int> InjectGraphCandidates(
        float[][] cosines, List<int> pooled, Dictionary<int, double> graphByRow, RecoGraphTuning tuning)
    {
        if (graphByRow.Count == 0 || tuning.MaxInjected <= 0)
        {
            return [];
        }

        var already = new HashSet<int>(pooled);
        var candidates = new List<(int Row, double Score)>();
        foreach (var (row, score) in graphByRow)
        {
            if (score >= tuning.MinInjectedScore
                && !already.Contains(row)
                && !float.IsNegativeInfinity(cosines[0][row]))
            {
                candidates.Add((row, score));
            }
        }

        if (candidates.Count > tuning.MaxInjected)
        {
            candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
            candidates.RemoveRange(tuning.MaxInjected, candidates.Count - tuning.MaxInjected);
        }

        return [.. candidates.Select(c => c.Row)];
    }

    /// <summary>
    /// Orders the scored pool and diversifies it. Relevance is min-max normalized over the pool
    /// because MMR subtracts a cosine from it and the hybrid score has no natural range; negative
    /// candidate-to-candidate cosines are clamped away for the same reason.
    /// </summary>
    private static List<Candidate> SelectWinners(
        VectorIndex index, List<Candidate> scored, int limit, double diversity,
        IReadOnlyCollection<long> seedIds, RecommenderTuning tuning)
    {
        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        // Collapsed on the sorted list and BEFORE the pool is trimmed, so a suppressed franchise
        // gives its slots back to other candidates rather than leaving the page short.
        scored = CollapseFranchises(scored, index, seedIds, tuning);
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
        Func<int, double> categoryWeight,
        TagMath.TagTree tagTree,
        double cutoff,
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
            $"SELECT id, {MangaBakaLocalStore.DisplayTitleSql("dump.series")}, cover_raw_url, year, " +
            "description, status, rating, total_chapters, genres, cover_x250_x1, cover_x250_x2 " +
            $"FROM dump.series WHERE id IN ({string.Join(",", ids)})";
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
                // Same weighting the ranking used, so the tags shown as matches are ordered by
                // what actually earned the score rather than by an unweighted echo of it.
                TagMath.Score(
                    index.TagsAt(winner.Row), tagProfile, idf, contributions, 1.0, categoryWeight,
                    tagTree);
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
                    BecauseOfTitle: winner.BestSeedQuery < 0 || winner.Distinctiveness <= cutoff
                        ? null
                        : queries[winner.BestSeedQuery].SeedTitle,
                    ThumbUrl: GetString(reader, 9),
                    ThumbUrlHiDpi: GetString(reader, 10),
                    CoRecommended: winner.CoRecommended,
                    CoRead: winner.CoRead,
                    TasteMatch: winner.TasteMatch);
            }
        }

        // Preserve the ranking the selection produced; the IN query returns rows in whatever order
        // SQLite likes.
        return ids.Select(byId.GetValueOrDefault).OfType<MangaBakaRecommendation>().ToList();
    }

    /// <summary>
    /// The genre and author profile, weighted by the same <paramref name="seedWeights"/> the centroid
    /// uses. Without them the structured half of the score was taste-blind: rating and reading
    /// history steered which query vectors got built and nothing else, while Genre and Tag together
    /// carry 2.5 of the score's ~7 points.
    ///
    /// <para>
    /// The share is taken over the seeds the dump actually returned, not over
    /// <paramref name="libraryIds"/>. A seed the dump has no row for contributes no genres, so
    /// counting it in the divisor shrank every genre in the profile by however many library series
    /// were missing from the catalogue.
    /// </para>
    ///
    /// <para>
    /// Authors stay unweighted, deliberately. The channel is a boolean — the candidate shares a
    /// creator or it doesn't — and there is no measurement behind turning it into a magnitude;
    /// lowering its coefficient for a single seed measured <em>worse</em>, not better.
    /// </para>
    /// </summary>
    private static async Task<(Dictionary<string, double> Genre, HashSet<string> Authors)>
        BuildProfileAsync(
            SqliteConnection conn,
            IReadOnlyCollection<long> libraryIds,
            IReadOnlyDictionary<long, double>? seedWeights,
            CancellationToken ct)
    {
        var genreWeight = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var authors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (libraryIds.Count == 0)
        {
            return (genreWeight, authors);
        }

        var perSeed = new List<(IReadOnlyList<string> Genres, double Weight)>(libraryIds.Count);
        var total = 0.0;

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                $"SELECT id, genres, authors FROM dump.series WHERE id IN ({string.Join(",", libraryIds)})";
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var weight = Math.Max(0, seedWeights?.GetValueOrDefault(reader.GetInt64(0), 1.0) ?? 1.0);
                perSeed.Add((ParseStringArray(GetString(reader, 1)), weight));
                total += weight;

                foreach (var a in ParseStringArray(GetString(reader, 2)))
                {
                    authors.Add(a);
                }
            }
        }

        if (total <= 0)
        {
            return (genreWeight, authors);
        }

        foreach (var (genres, weight) in perSeed)
        {
            var share = weight / total;
            foreach (var g in genres)
            {
                genreWeight[g] = genreWeight.GetValueOrDefault(g) + share;
            }
        }

        return (genreWeight, authors);
    }

    /// <summary>
    /// Dump ids that are the same work as a seed under a second entry. MangaBaka carries genuine
    /// duplicates - two <c>active</c> rows, same title, different id, <c>merged_with</c> null on
    /// both, so the dump's own dedupe does not cover them - and a duplicate of a seed is by
    /// construction the nearest thing in the catalogue to that seed. Left in, it takes a top slot
    /// and labels itself "feels like" the seed it is a copy of, which is how it was found.
    ///
    /// <para>
    /// Matched on <see cref="SeriesIdentity.NormalizeTitle"/>, exact only, for the same reason
    /// <c>SeriesIdentityService.AdoptOrphansAsync</c> refuses to fuzzy match: dropping one genuine
    /// recommendation is invisible, and the cost of a wrong match here is a title the user never
    /// gets shown. The SQL prefilter is a case-insensitive equality, so a duplicate whose title
    /// differs by punctuation alone is still missed - narrowing that needs a normalized column on
    /// the dump rather than a wider scan per request.
    /// </para>
    /// </summary>
    private static async Task<List<long>> GetDuplicateIdsAsync(
        SqliteConnection conn,
        IReadOnlyDictionary<long, string> seedTitles,
        HashSet<long> already,
        CancellationToken ct)
    {
        var duplicates = new List<long>();
        if (seedTitles.Count == 0)
        {
            return duplicates;
        }

        var wanted = seedTitles.Values
            .Select(SeriesIdentity.NormalizeTitle)
            .Where(t => t.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0)
        {
            return duplicates;
        }

        using var cmd = conn.CreateCommand();
        var names = new List<string>(seedTitles.Count);
        var i = 0;
        foreach (var title in seedTitles.Values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var name = $"$t{i++}";
            names.Add(name);
            cmd.Parameters.AddWithValue(name, title);
        }

        // state = 'active' matches ix_title_nocase's WHERE clause so the planner can use it; without
        // that the predicate is a full table scan (see the index's remarks for what that costs).
        cmd.CommandText =
            "SELECT id, title FROM dump.series " +
            $"WHERE state = 'active' AND title COLLATE NOCASE IN ({string.Join(",", names)})";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetInt64(0);
            if (!already.Contains(id) &&
                GetString(reader, 1) is { Length: > 0 } title &&
                wanted.Contains(SeriesIdentity.NormalizeTitle(title)))
            {
                duplicates.Add(id);
            }
        }

        return duplicates;
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
