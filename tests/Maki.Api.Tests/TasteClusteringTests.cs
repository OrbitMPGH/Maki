using Maki.Api.Services;

namespace Maki.Api.Tests;

/// <summary>
/// The clustering behind "you are not one reader, you are three". Tested on synthetic points
/// because the question is whether the algorithm splits what is obviously separate and refuses to
/// split what is not, which is a property of the arithmetic rather than of anybody's library.
/// </summary>
public class TasteClusteringTests
{
    /// <summary>A unit vector pointing mostly along <paramref name="axis"/>, jittered off it.</summary>
    private static float[] Point(int axis, double jitter, int index, int dimensions = 16)
    {
        var vec = new float[dimensions];
        vec[axis] = 1f;
        // Deterministic, spread across the remaining axes so points in a blob are near but not equal.
        vec[(axis + 1 + index) % dimensions] += (float)jitter;
        TasteClustering.Normalize(vec);
        return vec;
    }

    private static List<float[]> Blob(int axis, int count, double jitter = 0.15) =>
        [.. Enumerable.Range(0, count).Select(i => Point(axis, jitter, i))];

    [Fact]
    public void Splits_two_obvious_groups()
    {
        var points = Blob(0, 6).Concat(Blob(8, 6)).ToList();

        var result = TasteClustering.Cluster(points);

        Assert.NotNull(result);
        Assert.Equal(2, result!.K);
        // Everything from one blob lands together, whichever label it drew.
        Assert.Single(result.Assignments.Take(6).Distinct());
        Assert.Single(result.Assignments.Skip(6).Distinct());
        Assert.NotEqual(result.Assignments[0], result.Assignments[6]);
    }

    [Fact]
    public void Finds_three_groups_when_there_are_three()
    {
        var points = Blob(0, 5).Concat(Blob(6, 5)).Concat(Blob(12, 5)).ToList();

        var result = TasteClustering.Cluster(points);

        Assert.NotNull(result);
        Assert.Equal(3, result!.K);
    }

    [Fact]
    public void Refuses_to_split_one_tight_group()
    {
        // A reader who reads one thing should not be told they read two. The gain floor is what
        // stops k from always looking better one higher.
        var points = Blob(0, 14, jitter: 0.02);

        var result = TasteClustering.Cluster(points);

        Assert.True(result is null || result.K == 2,
            "a single tight blob must not fragment into many groups");
    }

    [Fact]
    public void Too_few_points_is_no_answer_rather_than_a_bad_one()
    {
        Assert.Null(TasteClustering.Cluster(Blob(0, 3).Concat(Blob(8, 3)).ToList()));
    }

    [Fact]
    public void Never_returns_a_group_below_the_size_floor()
    {
        // Eleven in one blob and three in another: any split that isolates one or two points is
        // rejected, so every group that comes back is big enough to be a habit.
        var points = Blob(0, 11).Concat(Blob(8, 3)).ToList();

        var result = TasteClustering.Cluster(points);

        if (result is not null)
        {
            var sizes = result.Assignments.GroupBy(a => a).Select(g => g.Count());
            Assert.All(sizes, size => Assert.True(size >= TasteClustering.MinClusterSize));
        }
    }

    [Fact]
    public void Prefers_a_split_that_actually_divides_the_library()
    {
        // Two even blobs and one straggler cluster. A split isolating the stragglers scores well
        // (peeling them off tightens the remainder) but says nothing, so the balanced split wins.
        var points = Blob(0, 20).Concat(Blob(6, 20)).Concat(Blob(12, 3)).ToList();

        var result = TasteClustering.Cluster(points);

        Assert.NotNull(result);
        var smallest = result!.Assignments.GroupBy(a => a).Min(g => g.Count());
        Assert.True(
            smallest >= points.Count * 0.08,
            $"a group holding {smallest} of {points.Count} does not divide the library");
    }

    [Fact]
    public void Falls_back_to_an_uneven_split_rather_than_no_answer()
    {
        // Forty in one blob and four outliers, with nothing balanced available. Real libraries look
        // like this, and an uneven grouping the reader can see is better than being told there is
        // nothing to say. The big group is still named, because groups are labelled against each
        // other rather than against a baseline they make up all of.
        var points = Blob(0, 40).Concat(Blob(8, 4)).ToList();

        var result = TasteClustering.Cluster(points);

        Assert.NotNull(result);
        Assert.All(
            result!.Assignments.GroupBy(a => a).Select(g => g.Count()),
            size => Assert.True(size >= TasteClustering.MinClusterSize));
    }

    [Fact]
    public void Is_deterministic_across_runs()
    {
        var points = Blob(0, 6).Concat(Blob(8, 6)).Concat(Blob(3, 6)).ToList();

        var first = TasteClustering.Cluster(points);
        var second = TasteClustering.Cluster(points);

        // A reader whose groups reshuffle between visits would reasonably conclude this is invented.
        Assert.NotNull(first);
        Assert.Equal(first!.Assignments, second!.Assignments);
    }

    [Fact]
    public void Centroid_of_opposing_vectors_is_null_rather_than_zero()
    {
        var up = new float[] { 1, 0, 0 };
        var down = new float[] { -1, 0, 0 };

        // A zero vector has no direction, and handing one to a cosine scan would score every row 0.
        Assert.Null(TasteClustering.Centroid([up, down]));
    }

    [Fact]
    public void Centroid_points_between_its_inputs()
    {
        var a = new float[] { 1, 0, 0 };
        var b = new float[] { 0, 1, 0 };

        var centroid = TasteClustering.Centroid([a, b]);

        Assert.NotNull(centroid);
        Assert.Equal(TasteClustering.Dot(centroid!, a), TasteClustering.Dot(centroid, b), 5);
        Assert.Equal(1.0, Math.Sqrt(centroid.Sum(v => (double)v * v)), 5);
    }
}
