using Maki.Api.Services;

namespace Maki.Api.Tests;

/// <summary>
/// The vector arithmetic the taste page's group centres are built from. Tested on synthetic points
/// because these are properties of the maths rather than of anybody's library.
/// </summary>
public class TasteClusteringTests
{
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

    [Fact]
    public void Normalize_refuses_a_zero_vector()
    {
        Assert.False(TasteClustering.Normalize(new float[4]));
    }
}
