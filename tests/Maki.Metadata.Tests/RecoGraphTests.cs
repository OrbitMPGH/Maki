using Maki.Metadata.RecoGraph;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Maki.Metadata.Tests;

/// <summary>
/// The co-recommendation graph, loaded from a real <c>reco-edges.db</c> and scored the way the
/// recommender scores it. The scorer's failure modes are the interesting part here: both were
/// measured on the real artifact before any of this was written, and both are the kind of thing
/// that looks fine in a spot check and ruins the channel in aggregate.
/// </summary>
public class RecoGraphTests : IDisposable
{
    private readonly string _dir;

    public RecoGraphTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "maki-recograph-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public async Task NoFile_IsNotAnError_ItIsJustNoGraph()
    {
        // The state every install starts in, and the one that has to stay silent: nothing is
        // published yet, so recommendations must work exactly as they did before this existed.
        var cache = Cache(Path.Combine(_dir, "nothing-here.db"));

        Assert.Null(await cache.GetAsync());
    }

    [Fact]
    public async Task AnUnreadableFile_DegradesToNoGraph_RatherThanTakingRecommendationsDown()
    {
        var path = Path.Combine(_dir, "corrupt.db");
        await File.WriteAllTextAsync(path, "this is not a database");

        Assert.Null(await Cache(path).GetAsync());
    }

    [Fact]
    public async Task PairsAreLoadedInBothDirections()
    {
        // The export stores each pair once, but "what is A paired with" and "what is B paired with"
        // both have to answer. A one-directional load would silently halve the graph.
        var graph = await Load((1, 2, 10), (1, 3, 20));

        Assert.Equal(3, graph!.Count);
        Assert.Equal(4, graph.EdgeCount);
        Assert.Equal(2, graph.DegreeAt(Node(graph, 1)));
        Assert.Equal(1, graph.DegreeAt(Node(graph, 2)));
        Assert.Equal([1L], Neighbours(graph, 2));
        Assert.Equal([2L, 3L], Neighbours(graph, 1).Order());
    }

    [Fact]
    public async Task VotesTravelWithTheEdge_InBothDirections()
    {
        var graph = await Load((1, 2, 42));

        Assert.Equal(42, graph!.VotesAt(Node(graph, 1))[0]);
        Assert.Equal(42, graph.VotesAt(Node(graph, 2))[0]);
    }

    [Fact]
    public async Task SeedsAreNeverScoredAsTheirOwnCandidates()
    {
        var graph = await Load((1, 2, 50), (1, 3, 50));

        var scores = RecoGraphScorer.Score(graph!, [1, 2], null, RecoGraphTuning.Default);

        Assert.Equal([3L], scores.Keys);
    }

    [Fact]
    public async Task EdgesUnderTheVoteFloor_DoNotCount()
    {
        var graph = await Load((1, 2, 1), (1, 3, 5));

        var scores = RecoGraphScorer.Score(graph!, [1], null, RecoGraphTuning.Default);

        Assert.Equal([3L], scores.Keys);
    }

    [Fact]
    public async Task TheTopScoreIsAlwaysOne_AndNothingRealIsEverZero()
    {
        // Normalization divides by the maximum rather than min-maxing, deliberately. A min-max would
        // floor the weakest real candidate at 0, making it indistinguishable from a series nobody
        // ever paired with anything — and 0 is exactly what the recommender reads as "no evidence",
        // which decides whether the relaxed cosine floor applies.
        var graph = await Load((1, 2, 500), (1, 3, 20), (1, 4, 3));

        var scores = RecoGraphScorer.Score(graph!, [1], null, RecoGraphTuning.Default);

        Assert.Equal(1.0, scores.Values.Max(), 6);
        Assert.All(scores.Values, v => Assert.True(v > 0, $"expected a positive score, got {v}"));
    }

    [Fact]
    public async Task AHub_IsPenalisedAgainstAFocusedTitle_WhichIsWhatStopsTheChannelBecomingAPopularityChart()
    {
        // Measured on the real artifact: with no degree penalty the top of every result is One
        // Piece, Bleach and Jujutsu Kaisen, because a handful of mega-titles are paired with
        // everything and carry vote counts that dwarf the rest. Hub is paired with 150 other
        // series, which is Chainsaw Man's real order of magnitude; Focused is paired only with the
        // seed, on half the votes, and must still win.
        //
        // The degree gap has to be that wide for the assertion to mean anything, because
        // DegreeSmoothing deliberately stops small degree differences mattering — 1 against 12 is
        // sampling noise on a partly-fetched catalogue, not evidence about how related two series
        // are.
        var pairs = new List<(long, long, int)> { (1, 2, 120), (1, 3, 60) };
        for (var other = 100; other < 250; other++)
        {
            pairs.Add((2, other, 120));
        }

        var graph = await Load([.. pairs]);
        var scores = RecoGraphScorer.Score(graph!, [1], null, RecoGraphTuning.Default);

        Assert.True(
            scores[3] > scores[2],
            $"focused title scored {scores[3]:F3}, hub scored {scores[2]:F3} — the hub penalty is not biting");

        // And with the penalty off, the hub's raw vote advantage wins, which is the failure being
        // guarded against rather than a hypothetical.
        var unpenalised = RecoGraphScorer.Score(
            graph!, [1], null, RecoGraphTuning.Default with { DegreePenalty = 0 });
        Assert.True(unpenalised[2] > unpenalised[3]);
    }

    [Fact]
    public async Task SeedWeightsScaleAContribution_SoAFavouritePullsHarderThanSomethingAbandoned()
    {
        var graph = await Load((1, 10, 100), (2, 11, 100));

        var scores = RecoGraphScorer.Score(
            graph!, [1, 2], new Dictionary<long, double> { [1] = 2.0, [2] = 0.5 }, RecoGraphTuning.Default);

        Assert.True(scores[10] > scores[11]);
    }

    [Fact]
    public async Task ASeedTheGraphHasNeverHeardOf_ScoresNothingRatherThanThrowing()
    {
        var graph = await Load((1, 2, 50));

        Assert.Empty(RecoGraphScorer.Score(graph!, [999], null, RecoGraphTuning.Default));
    }

    private RecoGraphCache Cache(string path) =>
        new(new RecoGraphOptions(path, _dir), NullLogger<RecoGraphCache>.Instance);

    private async Task<RecoGraphIndex?> Load(params (long A, long B, int Votes)[] pairs)
    {
        var path = Path.Combine(_dir, "reco-edges.db");
        using (var conn = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
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
        }

        return await Cache(path).GetAsync();
    }

    private static int Node(RecoGraphIndex graph, long id)
    {
        Assert.True(graph.TryGetNode(id, out var node), $"series {id} is not in the graph");
        return node;
    }

    private static long[] Neighbours(RecoGraphIndex graph, long id)
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
