using Maki.Metadata.CoRead;
using Maki.Metadata.Embedding;
using Maki.Metadata.RecoGraph;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Maki.Metadata.Tests;

/// <summary>
/// The co-read channel: what AniList readers actually finished together. Shares its storage with
/// the co-recommendation graph and almost nothing else, and the tests that matter here are the ones
/// pinning the differences — because the tempting move is to reuse that channel's scorer, and doing
/// so would normalize an already-normalized weight twice.
/// </summary>
public class CoReadGraphTests : IDisposable
{
    private readonly string _dir;

    public CoReadGraphTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "maki-coread-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public async Task NoFile_IsNotAnError_ItIsJustNoGraph()
    {
        // The state every install starts in and most stay in: nothing published, so recommendations
        // must behave exactly as they did before this channel existed.
        var cache = new CoReadCache(
            new CoReadOptions(Path.Combine(_dir, "nothing-here.db"), _dir),
            NullLogger<CoReadCache>.Instance);

        Assert.Null(await cache.GetAsync());
    }

    [Fact]
    public async Task AnUnreadableFile_DegradesToNoGraph()
    {
        var path = Path.Combine(_dir, "corrupt.db");
        await File.WriteAllTextAsync(path, "this is not a database");

        Assert.Null(await Cache(path).GetAsync());
    }

    [Fact]
    public async Task PairsAreLoadedInBothDirections()
    {
        var graph = await Load((1, 2, 0.5f), (1, 3, 0.25f));

        Assert.Equal(3, graph!.Count);
        Assert.Equal(4, graph.EdgeCount);
        Assert.Equal(2, graph.DegreeAt(Node(graph, 1)));
        Assert.Equal([1L], Neighbours(graph, 2));
    }

    [Fact]
    public async Task StrengthSurvivesTheRoundTripAsAFloat()
    {
        // The vote graph stores integers. Truncating a strength to one would zero every edge in the
        // file, and the channel would go silent without failing.
        var graph = await Load((1, 2, 0.0125f));

        var node = Node(graph!, 1);
        Assert.Equal(0.0125f, graph!.WeightsAt(node)[0], 5);
    }

    [Fact]
    public async Task SelfPairsAreDropped()
    {
        // A series cannot corroborate itself, and a self-loop would score a seed as its own
        // candidate.
        var graph = await Load((1, 1, 0.9f), (1, 2, 0.4f));

        Assert.Equal(2, graph!.Count);
        Assert.Equal([2L], Neighbours(graph, 1));
    }

    [Fact]
    public async Task NonFiniteAndZeroStrengthsAreDropped()
    {
        // A NaN propagates through every sum it reaches, so it must never enter the index.
        var graph = await Load((1, 2, float.NaN), (1, 3, 0f), (1, 4, 0.3f));

        Assert.Equal([4L], Neighbours(graph, 1));
    }

    [Fact]
    public async Task ScoresSumAcrossSeedsAndNormalizeToTheBest()
    {
        // 5 is reached from both seeds, 4 from one. Normalization is by the maximum, never min-max:
        // a min-max would floor the weakest real candidate at 0, which is exactly the value callers
        // read as "no co-read evidence".
        var graph = await Load((1, 5, 0.4f), (2, 5, 0.4f), (1, 4, 0.4f));

        var scores = CoReadScorer.Score(graph!, [1L, 2L], null, CoReadTuning.Default);

        Assert.Equal(1.0, scores[5], 5);
        Assert.Equal(0.5, scores[4], 5);
    }

    [Fact]
    public async Task SeedsAreNeverScoredAsCandidates()
    {
        var graph = await Load((1, 2, 0.9f), (1, 3, 0.2f));

        var scores = CoReadScorer.Score(graph!, [1L, 2L], null, CoReadTuning.Default);

        Assert.DoesNotContain(1L, scores.Keys);
        Assert.DoesNotContain(2L, scores.Keys);
        Assert.Contains(3L, scores.Keys);
    }

    [Fact]
    public async Task SeedWeightsScaleContributions()
    {
        var graph = await Load((1, 10, 0.5f), (2, 20, 0.5f));

        var scores = CoReadScorer.Score(
            graph!, [1L, 2L], new Dictionary<long, double> { [1] = 1.0, [2] = 0.25 },
            CoReadTuning.Default);

        Assert.Equal(1.0, scores[10], 5);
        Assert.Equal(0.25, scores[20], 5);
    }

    [Fact]
    public async Task DegreeDoesNotPenalize_BecauseTheStrengthAlreadyDividedByPopularity()
    {
        // This is the whole reason CoReadScorer exists rather than reusing RecoGraphScorer. The hub
        // here is paired with 200 series; the loner with one. Both link a seed at identical
        // strength, so both must score identically: the build already divided every co-occurrence
        // by sqrt((users(a)+k)(users(b)+k)), and dividing again by degree is the double
        // normalization measured on the vote graph at DegreePenalty 0.5, where the channel inverted
        // into single-edge obscurities.
        var pairs = new List<(long, long, float)> { (1, 100, 0.5f), (1, 200, 0.5f) };
        for (var i = 0; i < 200; i++)
        {
            pairs.Add((100, 1000 + i, 0.1f));
        }

        var graph = await Load([.. pairs]);
        var scores = CoReadScorer.Score(graph!, [1L], null, CoReadTuning.Default);

        Assert.True(graph!.DegreeAt(Node(graph, 100)) > 200);
        Assert.Equal(1, graph.DegreeAt(Node(graph, 200)));
        Assert.Equal(scores[100], scores[200], 5);
    }

    [Fact]
    public async Task MinStrengthExcludesWeakEdges()
    {
        var graph = await Load((1, 2, 0.5f), (1, 3, 0.01f));

        var scores = CoReadScorer.Score(
            graph!, [1L], null, CoReadTuning.Default with { MinStrength = 0.1 });

        Assert.Contains(2L, scores.Keys);
        Assert.DoesNotContain(3L, scores.Keys);
    }

    [Fact]
    public async Task NoSeedInTheGraph_ScoresNothing()
    {
        var graph = await Load((1, 2, 0.5f));

        Assert.Empty(CoReadScorer.Score(graph!, [999L], null, CoReadTuning.Default));
    }

    [Fact]
    public void Injection_RespectsTheCorroborationFloorAndTheCap()
    {
        var cosines = new[] { new float[10] };
        Array.Fill(cosines[0], 0.5f);

        var byRow = new Dictionary<int, double> { [1] = 1.0, [2] = 0.9, [3] = 0.5, [4] = 0.8 };
        var tuning = CoReadTuning.Default with { MinInjectedScore = 0.7, MaxInjected = 2 };

        var injected = SemanticRecommender.InjectCoReadCandidates(cosines, [], [], byRow, tuning);

        // 3 fails the floor; 1 and 2 outrank 4 for the two slots.
        Assert.Equal(2, injected.Count);
        Assert.Contains(1, injected);
        Assert.Contains(2, injected);
    }

    [Fact]
    public void Injection_SkipsRowsThatFailedTheFilters()
    {
        // Scan writes negative infinity into every channel for a row the filter plan, exclusion set
        // or required tags rejected. Testing that sentinel is the same predicate FuseByRank uses,
        // so no filter logic is duplicated and there is no way to smuggle a row past a filter.
        var cosines = new[] { new float[5] };
        Array.Fill(cosines[0], 0.5f);
        cosines[0][2] = float.NegativeInfinity;

        var byRow = new Dictionary<int, double> { [2] = 1.0, [3] = 1.0 };

        var injected = SemanticRecommender.InjectCoReadCandidates(
            cosines, [], [], byRow, CoReadTuning.Default);

        Assert.Equal([3], injected);
    }

    [Fact]
    public void Injection_DoesNotSpendItsCapOnRowsAnotherChannelAlreadyBroughtIn()
    {
        // A row both graphs vouch for is already in the pool. Letting it take a slot here would
        // silently shrink this channel's real cap, which is the dial that controls its intensity.
        var cosines = new[] { new float[10] };
        Array.Fill(cosines[0], 0.5f);

        var byRow = new Dictionary<int, double> { [1] = 1.0, [2] = 1.0, [3] = 1.0 };

        var injected = SemanticRecommender.InjectCoReadCandidates(
            cosines, pooled: [1], alreadyInjected: [2], byRow, CoReadTuning.Default);

        Assert.Equal([3], injected);
    }

    [Fact]
    public void HybridScore_AddsTheTwoCrowdTermsSeparately()
    {
        // They answer different questions and disagree on most pairs, so agreement is real
        // corroboration and is paid for twice on purpose.
        var w = new EmbeddingMath.Weights(Graph: 0.6, CoRead: 0.4);

        var neither = EmbeddingMath.HybridScore(0.5, 0, 0, false, 0, 0, 0.5, w);
        var both = EmbeddingMath.HybridScore(0.5, 0, 0, false, 0, 0, 0.5, w, 1.0, 1.0);

        Assert.Equal(1.0, both - neither, 5);
    }

    [Fact]
    public void HybridScore_CostsNothingWhenThereIsNoEvidence()
    {
        // The common case by far: most candidates are in neither graph, and they must score exactly
        // as they did before either channel existed.
        var w = new EmbeddingMath.Weights(Graph: 0.6, CoRead: 0.4);

        Assert.Equal(
            EmbeddingMath.HybridScore(0.5, 0.2, 0.3, true, 70, 0, 0.5, w),
            EmbeddingMath.HybridScore(0.5, 0.2, 0.3, true, 70, 0, 0.5, w, 0, 0),
            10);
    }

    private CoReadCache Cache(string path) =>
        new(new CoReadOptions(path, _dir), NullLogger<CoReadCache>.Instance);

    private async Task<PairGraphIndex?> Load(params (long A, long B, float Strength)[] pairs)
    {
        var path = Path.Combine(_dir, "coread-edges.db");
        if (File.Exists(path))
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }

        using (var conn = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE pair (
                    a_id INTEGER NOT NULL, b_id INTEGER NOT NULL,
                    support INTEGER NOT NULL DEFAULT 3, strength REAL NOT NULL,
                    PRIMARY KEY (a_id, b_id)) WITHOUT ROWID;
                """ + $"""
                INSERT INTO pair (a_id, b_id, strength) VALUES
                {string.Join(",", pairs.Select(p =>
                    $"({p.A}, {p.B}, {(float.IsNaN(p.Strength) ? "'nan'" : p.Strength.ToString("R", System.Globalization.CultureInfo.InvariantCulture))})"))};
                """;
            cmd.ExecuteNonQuery();
        }

        return await Cache(path).GetAsync();
    }

    private static int Node(PairGraphIndex graph, long id)
    {
        Assert.True(graph.TryGetNode(id, out var node), $"series {id} is not in the graph");
        return node;
    }

    private static long[] Neighbours(PairGraphIndex graph, long id)
    {
        var node = Node(graph, id);
        var neighbours = graph.NeighboursAt(node);
        var ids = new long[neighbours.Length];
        for (var i = 0; i < neighbours.Length; i++)
        {
            ids[i] = graph.IdAt(neighbours[i]);
        }

        return ids;
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
