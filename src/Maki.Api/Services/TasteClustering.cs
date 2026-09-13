namespace Maki.Api.Services;

/// <summary>
/// The vector arithmetic the taste page runs on unit-length embedding vectors.
///
/// <para>
/// Everything here is cosine-shaped because the index's vectors are unit-normalized and every other
/// consumer scores them that way: on unit vectors a dot product <em>is</em> the cosine, so a centre
/// only has to be renormalized after summing.
/// </para>
///
/// <para>
/// Free of every Maki type so it can be tested on synthetic points, and separate from
/// <see cref="TasteInsightsService"/> for the same reason.
/// </para>
/// </summary>
public static class TasteClustering
{
    /// <summary>Plain dot product, which on unit-length vectors is the cosine.</summary>
    public static double Dot(float[] a, float[] b)
    {
        var sum = 0.0;
        var length = Math.Min(a.Length, b.Length);
        for (var d = 0; d < length; d++)
        {
            sum += a[d] * b[d];
        }

        return sum;
    }

    /// <summary>Scales in place to unit length. False when the vector is all zeroes.</summary>
    public static bool Normalize(float[] vec)
    {
        var norm = Math.Sqrt(vec.Sum(v => (double)v * v));
        if (norm <= 1e-9)
        {
            return false;
        }

        for (var d = 0; d < vec.Length; d++)
        {
            vec[d] = (float)(vec[d] / norm);
        }

        return true;
    }

    /// <summary>The unit-length mean of a set of vectors, or null when they cancel out.</summary>
    public static float[]? Centroid(IReadOnlyList<float[]> vectors)
    {
        if (vectors.Count == 0)
        {
            return null;
        }

        var sum = new float[vectors[0].Length];
        foreach (var v in vectors)
        {
            for (var d = 0; d < sum.Length; d++)
            {
                sum[d] += v[d];
            }
        }

        return Normalize(sum) ? sum : null;
    }
}
