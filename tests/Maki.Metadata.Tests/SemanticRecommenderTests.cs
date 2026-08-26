using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Maki.Metadata.CoRead;
using Maki.Metadata.RecoGraph;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Maki.Metadata.Tests;

/// <summary>
/// End-to-end over a tiny fake dump plus a real vector store, so the retrieval shape is exercised
/// through the same code path the app uses (index build included) rather than against a stub.
/// </summary>
public class SemanticRecommenderTests : IDisposable
{
    private const int Dim = 16;

    private readonly string _dir;
    private readonly string _dumpPath;
    private readonly string _vectorPath;
    private readonly List<string> _rows = [];

    public SemanticRecommenderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "maki-semreco-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dumpPath = Path.Combine(_dir, "mangabaka.db");
        _vectorPath = Path.Combine(_dir, "embeddings.db");
    }

    [Fact]
    public async Task ASeedWhoseTasteIsTwoThings_StillSurfacesAMatchForEitherHalf()
    {
        // Two seeds pointing in unrelated directions, so their centroid sits near neither. Twin is
        // a ~0.997 match for one seed but only ~0.70 against that centroid, while a crowd of
        // middling candidates sits at ~0.90 against the centroid and ~0.64 against either seed.
        // Ranked by the centroid alone — what a single mean-vector query does — every middling
        // candidate outranks Twin and it never makes the page, which is the dilution being fixed.
        var alpha = Axis(0);
        var beta = Axis(1);
        var centroid = Blend(alpha, beta);

        var vectors = new List<(long Id, string Hash, float[] Vector)>
        {
            (1, "h", alpha),
            (2, "h", beta),
            (10, "h", Nudge(alpha, 2, 0.08f)),
        };
        Add(1, "Alpha");
        Add(2, "Beta");
        Add(10, "Twin of Alpha");
        for (var i = 0; i < 8; i++)
        {
            Add(100 + i, $"Middling {i}");
            vectors.Add((100 + i, "h", Nudge(centroid, 3 + i, 0.48f)));
        }

        WriteDump();
        Store().UpsertBatch(vectors);

        var picks = await Recommender().GetSimilarAsync([1, 2], [], limit: 3);

        Assert.Equal("10", picks[0].ProviderId);
        // And the attribution names the seed that actually drove it, not the whole seed set.
        Assert.Equal("Alpha", picks[0].BecauseOfTitle);
        // The middlings are still recommendable, just no longer ahead of a better match.
        Assert.Equal(3, picks.Count);
        Assert.All(picks.Skip(1), p => Assert.StartsWith("Middling", p.Title));
    }

    [Fact]
    public async Task Seeds_AndExplicitExclusions_AreNeverRecommendedBack()
    {
        Add(1, "Alpha");
        Add(10, "Excluded");
        Add(11, "Wanted");
        WriteDump();
        Store().UpsertBatch([
            (1L, "h", Axis(0)),
            (10L, "h", Nudge(Axis(0), 2, 0.1f)),
            (11L, "h", Nudge(Axis(0), 3, 0.1f)),
        ]);

        var picks = await Recommender().GetSimilarAsync([1], [10], limit: 10);

        Assert.Equal(["11"], picks.Select(p => p.ProviderId));
    }

    [Fact]
    public async Task Diversity_SwapsANearDuplicateForSomethingElseThatMatches()
    {
        // Pair and PairTwin are all but the same vector; Other is a weaker match but unrelated to
        // both. At diversity 0 the pair takes both slots, which is the homogeneity complaint.
        var seed = Spread(0, 1, 2, 3);
        Add(1, "Seed");
        Add(10, "Pair");
        Add(11, "Pair Twin");
        Add(12, "Other");
        WriteDump();
        Store().UpsertBatch([
            (1L, "h", seed),
            (10L, "h", Spread(0, 1, 2)),
            (11L, "h", Nudge(Spread(0, 1, 2), 8, 0.02f)),
            (12L, "h", Blend(Axis(3), Scaled(Axis(0), 0.3f))),
        ]);

        var packed = await Recommender().GetSimilarAsync([1], [], limit: 2);
        var spread = await Recommender().GetSimilarAsync([1], [], limit: 2, diversity: 0.6);

        Assert.Equal(["10", "11"], packed.Select(p => p.ProviderId));
        Assert.Equal(["10", "12"], spread.Select(p => p.ProviderId));
    }

    [Fact]
    public async Task FiltersAreAppliedBeforeTheTopK_SoAFilteredQueryIsNotTruncatedToNothing()
    {
        // Every close match is a manhwa; the one manga is the distant candidate. A filter applied
        // to an already-chosen top-K would leave nothing.
        Add(1, "Seed");
        for (var i = 0; i < 6; i++)
        {
            Add(10 + i, $"Near {i}", type: "manhwa");
        }

        Add(50, "Distant Manga");
        WriteDump();
        var vectors = new List<(long, string, float[])> { (1L, "h", Axis(0)), (50L, "h", Spread(0, 1, 2)) };
        for (var i = 0; i < 6; i++)
        {
            vectors.Add((10 + i, "h", Nudge(Axis(0), 4 + i, 0.05f)));
        }

        Store().UpsertBatch(vectors);

        var picks = await Recommender().GetSimilarAsync(
            [1], [], limit: 3, new RecommendationFilters(Types: ["manga"]));

        Assert.Equal(["50"], picks.Select(p => p.ProviderId));
    }

    [Fact]
    public async Task OneSeed_DefaultWeightsLetSharedGenresOutrankFeel_ReducedOnesDont()
    {
        // The whole reason GetSimilarAsync takes a weights override. BuildProfileAsync spreads
        // 1/seedCount over each seed genre, so at one seed every genre carries a full 1.0 — sharing
        // three of them pays 3.0, more than the semantic channel can pay for a near-perfect cosine.
        // GenreTwin is a poor match (~0.45) that shares all three genres and the author; FeelMatch is
        // an excellent one (~0.95) that shares neither.
        Add(1, "Seed", genres: """["Action","Drama","Fantasy"]""");
        Add(10, "Genre Twin", genres: """["Action","Drama","Fantasy"]""");
        Add(11, "Feel Match", genres: """["Sports"]""", authors: """["Someone Else"]""");
        WriteDump();
        Store().UpsertBatch([
            (1L, "h", Axis(0)),
            (10L, "h", Spread(0, 1, 2, 3, 4)),
            (11L, "h", Nudge(Axis(0), 5, 0.33f)),
        ]);

        var byDefault = await Recommender().GetSimilarAsync([1], [], limit: 2);
        var reduced = await Recommender().GetSimilarAsync(
            [1], [], limit: 2, weights: new EmbeddingMath.Weights(Genre: 0.15, Author: 0.25));

        Assert.Equal(["10", "11"], byDefault.Select(p => p.ProviderId));
        Assert.Equal(["11", "10"], reduced.Select(p => p.ProviderId));
    }

    [Fact]
    public async Task OneSeed_AttributesNothing_BecauseTheCentroidIsTheSeed()
    {
        // With a single seed the centroid *is* that seed's vector, so BuildQueries emits it alone and
        // BecauseOfTitle stays null. "Feels like <the one series you asked about>" would be noise, and
        // the duplicate per-seed query would double the scan to produce it.
        Add(1, "Seed");
        Add(10, "Candidate");
        WriteDump();
        Store().UpsertBatch([(1L, "h", Axis(0)), (10L, "h", Nudge(Axis(0), 2, 0.1f))]);

        var picks = await Recommender().GetSimilarAsync([1], [], limit: 5);

        Assert.Equal(["10"], picks.Select(p => p.ProviderId));
        Assert.Null(picks[0].BecauseOfTitle);
    }

    [Fact]
    public async Task NoIndexYet_ReturnsNothingSoTheCallerCanFallBack()
    {
        Add(1, "Seed");
        WriteDump();
        Store(); // schema only — no vectors

        Assert.Empty(await Recommender().GetSimilarAsync([1], [], limit: 5));
    }

    [Fact]
    public async Task ACoRecommendedCandidate_OutranksACloserFeelMatch()
    {
        // The point of the whole channel, in one case. Vagabond is not what Berserk's *description*
        // is nearest to, which is exactly why the embeddings rank it below something blander and
        // why readers do not. Closer wins on cosine (~0.86 against ~0.74); the vote graph wins anyway,
        // because the seed's readers overwhelmingly went on to it.
        Add(1, "Seed");
        Add(10, "Closer by feel");
        Add(11, "Co-read");
        WriteDump();
        Store().UpsertBatch([
            (1L, "h", Axis(0)),
            (10L, "h", Nudge(Axis(0), 2, 0.6f)),
            (11L, "h", Nudge(Axis(0), 3, 0.9f)),
        ]);

        var withoutGraph = await Recommender().GetSimilarAsync([1], [], limit: 5);
        var withGraph = await Recommender(WriteGraph((1, 11, 500))).GetSimilarAsync([1], [], limit: 5);

        Assert.Equal(["10", "11"], withoutGraph.Select(p => p.ProviderId));
        Assert.Equal(["11", "10"], withGraph.Select(p => p.ProviderId));
        // And it says so, since a pick that reads as the weaker match needs explaining.
        Assert.True(withGraph[0].CoRecommended);
        Assert.False(withGraph[1].CoRecommended);
    }

    [Fact]
    public async Task TheSameCandidate_IsStillDroppedByAFilter_BecauseInjectionRespectsThePlan()
    {
        // The injection path reads the scan's own negative-infinity sentinel rather than re-testing
        // the filters, so a filter it never learned about must still bind. If this ever fails, the
        // channel has become a way to smuggle rows past RecommendationFilters.
        Add(1, "Seed");
        Add(10, "Co-read", type: "manhwa");
        WriteDump();
        Store().UpsertBatch([(1L, "h", Axis(0)), (10L, "h", Nudge(Axis(0), 3, 0.9f))]);

        var graph = WriteGraph((1, 10, 500));

        Assert.Empty(await Recommender(graph).GetSimilarAsync(
            [1], [], limit: 5, new RecommendationFilters(Types: ["manga"])));

        // Same series, same graph, no filter: proving the emptiness above is the filter's doing and
        // not the candidate quietly failing to qualify for some other reason.
        Assert.Equal(
            ["10"],
            (await Recommender(graph).GetSimilarAsync([1], [], limit: 5)).Select(p => p.ProviderId));
    }

    [Fact]
    public async Task ASeedIsNeverRecommendedBack_EvenWhenTheGraphPairsItWithAnotherSeed()
    {
        Add(1, "Alpha");
        Add(2, "Beta");
        Add(10, "Candidate");
        WriteDump();
        Store().UpsertBatch([
            (1L, "h", Axis(0)),
            (2L, "h", Axis(1)),
            (10L, "h", Nudge(Axis(0), 2, 0.1f)),
        ]);

        var picks = await Recommender(WriteGraph((1, 2, 900), (1, 10, 40)))
            .GetSimilarAsync([1, 2], [], limit: 5);

        Assert.Equal(["10"], picks.Select(p => p.ProviderId));
    }

    [Fact]
    public async Task AnEdgeUnderTheVoteFloor_IsNotEvidence()
    {
        // One person clicking "recommend" is noise, and the long tail of the real artifact is
        // single-vote pairs — median 2 against a maximum of 6008. Same setup as the reordering test
        // above, but the edge carries one vote, so the order must not move.
        Add(1, "Seed");
        Add(10, "Closer by feel");
        Add(11, "Barely paired");
        WriteDump();
        Store().UpsertBatch([
            (1L, "h", Axis(0)),
            (10L, "h", Nudge(Axis(0), 2, 0.6f)),
            (11L, "h", Nudge(Axis(0), 3, 0.9f)),
        ]);

        var graph = WriteGraph((1, 11, 1));

        var ignored = await Recommender(graph).GetSimilarAsync([1], [], limit: 5);
        var counted = await Recommender(graph, RecoGraphTuning.Default with { MinVotes = 1 })
            .GetSimilarAsync([1], [], limit: 5);

        Assert.Equal(["10", "11"], ignored.Select(p => p.ProviderId));
        Assert.All(ignored, p => Assert.False(p.CoRecommended));
        Assert.Equal(["11", "10"], counted.Select(p => p.ProviderId));
    }

    [Fact]
    public void Injection_CapsItself_AndTakesTheBestVouchedRowsFirst()
    {
        // Tested directly rather than through GetSimilarAsync: the RRF pool is 200 rows deep, so a
        // fixture would need a bigger catalogue than that before anything is *injected* at all
        // rather than simply pooled, and at that size the assertion stops being about the cap.
        // Row 3 is the sentinel case — it failed the filter plan, so the scan wrote negative
        // infinity and it must not come back however well the graph vouches for it.
        var cosines = new[] { new[] { 0.9f, 0.2f, 0.2f, float.NegativeInfinity, 0.2f, 0.2f } };
        var graph = new Dictionary<int, double>
        {
            [1] = 0.65, [2] = 0.90, [3] = 1.00, [4] = 0.10, [5] = 0.70,
        };

        var open = RecoGraphTuning.Default with { MaxInjected = 10, MinInjectedScore = 0 };
        var capped = SemanticRecommender.InjectGraphCandidates(cosines, [0], graph, open with { MaxInjected = 2 });
        var uncapped = SemanticRecommender.InjectGraphCandidates(cosines, [0], graph, open);
        var corroborated = SemanticRecommender.InjectGraphCandidates(cosines, [0], graph, open with { MinInjectedScore = 0.60 });

        Assert.Equal([2, 5], capped);
        Assert.Equal([1, 2, 4, 5], uncapped.Order());
        // Row 4 is the thin-evidence case the real library surfaced: real, but nowhere near enough
        // to earn a place the cosine ranking never gave it.
        Assert.Equal([1, 2, 5], corroborated.Order());
    }

    [Fact]
    public async Task ACandidateWithNoEdges_ScoresIdenticallyWhetherOrNotAGraphIsInstalled()
    {
        // The channel is a bonus, never a gate: three quarters of the catalogue has no edge at all,
        // and those series must rank exactly where they ranked before this existed.
        Add(1, "Seed");
        Add(10, "Unpaired A");
        Add(11, "Unpaired B");
        Add(12, "Paired elsewhere");
        WriteDump();
        Store().UpsertBatch([
            (1L, "h", Axis(0)),
            (10L, "h", Nudge(Axis(0), 2, 0.10f)),
            (11L, "h", Nudge(Axis(0), 3, 0.12f)),
            (12L, "h", Nudge(Axis(0), 4, 0.14f)),
        ]);

        var without = await Recommender().GetSimilarAsync([1], [], limit: 5);
        // An edge between two candidates, touching no seed, so nothing here should move.
        var with = await Recommender(WriteGraph((11, 12, 800))).GetSimilarAsync([1], [], limit: 5);

        Assert.Equal(without.Select(p => p.ProviderId), with.Select(p => p.ProviderId));
        Assert.All(with, p => Assert.False(p.CoRecommended));
    }

    private void Add(
        long id, string title, string type = "manga", string genres = """["Action"]""",
        string authors = """["Author"]""") =>
        _rows.Add(
            $"({id}, 'active', 80, 'safe', '{type}', 'completed', 2000, '{title}', " +
            $"'http://c/{id}.jpg', 'desc', '12', '{genres}', '{authors}', {id})");

    private void WriteDump()
    {
        using var conn = new SqliteConnection($"Data Source={_dumpPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE series (
                id INTEGER PRIMARY KEY, state TEXT, rating REAL, content_rating TEXT, type TEXT,
                status TEXT, year INTEGER, title TEXT, titles TEXT, cover_raw_url TEXT, description TEXT,
                total_chapters TEXT, genres TEXT, authors TEXT, popularity_global_current INTEGER,
                -- The pre-sized thumbnail columns the hydrate query reads. Named here rather than
                -- listed in the INSERT, so adding a column to the dump doesn't mean editing every
                -- row literal in this file.
                cover_x250_x1 TEXT, cover_x250_x2 TEXT);
            """ + $"""
            INSERT INTO series (
                id, state, rating, content_rating, type, status, year, title, cover_raw_url,
                description, total_chapters, genres, authors, popularity_global_current)
            VALUES {string.Join(",", _rows)};
            """;
        cmd.ExecuteNonQuery();
    }

    private EmbeddingOptions Options() =>
        new(_dir, _vectorPath, _dir, EmbeddingModelProfile.Base with { Dimensions = Dim }) { Enabled = true };

    private EmbeddingStore Store()
    {
        var store = new EmbeddingStore(Options());
        store.EnsureSchema();
        return store;
    }

    /// <summary>
    /// A recommender over this fixture's dump and vector store. <paramref name="graphPath"/> defaults
    /// to a file that does not exist, which is the shipping default state: no artifact installed, so
    /// the co-recommendation channel contributes nothing and every pre-existing assertion here is
    /// still testing the behaviour it was written for.
    /// </summary>
    private SemanticRecommender Recommender(
        string? graphPath = null,
        RecoGraphTuning? graphTuning = null,
        string? coReadPath = null,
        CoReadTuning? coReadTuning = null)
    {
        var dump = new MangaBakaDumpOptions(_dumpPath, _dir);
        var graph = new RecoGraphOptions(graphPath ?? Path.Combine(_dir, "absent-reco-edges.db"), _dir);
        var coRead = new CoReadOptions(coReadPath ?? Path.Combine(_dir, "absent-coread-edges.db"), _dir);
        return new SemanticRecommender(
            Options(),
            dump,
            new EmbeddingStore(Options()),
            new VectorIndexCache(Options(), dump, NullLogger<VectorIndexCache>.Instance),
            new RecoGraphCache(graph, NullLogger<RecoGraphCache>.Instance),
            graphTuning ?? RecoGraphTuning.Default,
            new CoReadCache(coRead, NullLogger<CoReadCache>.Instance),
            coReadTuning ?? CoReadTuning.Default,
            NullLogger<SemanticRecommender>.Instance);
    }

    /// <summary>
    /// Writes a <c>reco-edges.db</c> holding the given unordered pairs and returns its path. Same
    /// schema <c>distribution/fetch-reco-graph.cs</c> exports.
    /// </summary>
    private string WriteGraph(params (long A, long B, int Votes)[] pairs)
    {
        var path = Path.Combine(_dir, "reco-edges.db");
        using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE pair (
                a_id INTEGER NOT NULL, b_id INTEGER NOT NULL,
                anilist_votes INTEGER NOT NULL DEFAULT 0, mal_votes INTEGER NOT NULL DEFAULT 0,
                directions INTEGER NOT NULL DEFAULT 1, PRIMARY KEY (a_id, b_id)) WITHOUT ROWID;
            """ + $"""
            INSERT INTO pair (a_id, b_id, anilist_votes) VALUES
            {string.Join(",", pairs.Select(p => $"({p.A}, {p.B}, {p.Votes})"))};
            """;
        cmd.ExecuteNonQuery();
        return path;
    }

    private static float[] Axis(int i)
    {
        var v = new float[Dim];
        v[i] = 1f;
        return v;
    }

    /// <summary>A unit vector with equal weight on each named axis.</summary>
    private static float[] Spread(params int[] axes)
    {
        var v = new float[Dim];
        foreach (var a in axes)
        {
            v[a] = 1f;
        }

        EmbeddingMath.NormalizeInPlace(v);
        return v;
    }

    private static float[] Blend(float[] a, float[] b) => EmbeddingMath.Mean([a, b])!;

    private static float[] Scaled(float[] v, float by) => v.Select(x => x * by).ToArray();

    /// <summary>The vector, tilted slightly onto one other axis, so candidates aren't identical.</summary>
    private static float[] Nudge(float[] v, int axis, float amount)
    {
        var copy = (float[])v.Clone();
        copy[axis] += amount;
        EmbeddingMath.NormalizeInPlace(copy);
        return copy;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
