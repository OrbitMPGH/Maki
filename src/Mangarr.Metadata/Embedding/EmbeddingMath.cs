using System.Numerics.Tensors;
using System.Runtime.InteropServices;

namespace Mangarr.Metadata.Embedding;

/// <summary>
/// Pure vector helpers for the embedding pipeline: L2 normalization, cosine similarity,
/// float[]↔blob codec, and the hybrid recommendation score. Kept dependency-free and
/// unit-tested so the scoring is verifiable without a model.
/// </summary>
public static class EmbeddingMath
{
    /// <summary>Normalizes a vector to unit length in place (no-op for a zero vector).</summary>
    public static void NormalizeInPlace(float[] vec)
    {
        var norm = MathF.Sqrt(TensorPrimitives.Dot(vec, vec));
        if (norm <= 1e-8f)
        {
            return;
        }

        for (var i = 0; i < vec.Length; i++)
        {
            vec[i] /= norm;
        }
    }

    /// <summary>Cosine similarity. Assumes both vectors are already unit-normalized (dot == cosine).</summary>
    public static float Cosine(float[] a, float[] b) =>
        a.Length == b.Length ? TensorPrimitives.Dot(a, b) : 0f;

    /// <summary>
    /// Index of the seed vector most similar to <paramref name="candidate"/> (highest cosine);
    /// -1 if <paramref name="seeds"/> is empty. Used to attribute a semantic pick to the one
    /// seed whose "feel" drove it.
    /// </summary>
    public static int MostSimilar(float[] candidate, IReadOnlyList<float[]> seeds)
    {
        var best = -1;
        var bestSim = float.NegativeInfinity;
        for (var i = 0; i < seeds.Count; i++)
        {
            var sim = Cosine(candidate, seeds[i]);
            if (sim > bestSim)
            {
                bestSim = sim;
                best = i;
            }
        }

        return best;
    }

    /// <summary>Mean of several unit vectors, re-normalized — the seed vector for a set of series.</summary>
    public static float[]? Mean(IReadOnlyList<float[]> vectors)
    {
        if (vectors.Count == 0)
        {
            return null;
        }

        var dim = vectors[0].Length;
        var sum = new float[dim];
        foreach (var v in vectors)
        {
            if (v.Length != dim)
            {
                continue;
            }

            TensorPrimitives.Add(sum, v, sum);
        }

        NormalizeInPlace(sum);
        return sum;
    }

    /// <summary>
    /// Weighted mean of several unit vectors, re-normalized. Each vector contributes in proportion
    /// to its weight (a highly-rated seed pulls the seed vector toward its "feel"); non-positive or
    /// mismatched-dimension entries are skipped. Null when nothing contributes.
    /// </summary>
    public static float[]? WeightedMean(IReadOnlyList<(float[] Vec, double Weight)> weighted)
    {
        if (weighted.Count == 0)
        {
            return null;
        }

        var dim = weighted[0].Vec.Length;
        var sum = new float[dim];
        var contributed = false;
        foreach (var (v, weight) in weighted)
        {
            if (v.Length != dim || weight <= 0)
            {
                continue;
            }

            for (var i = 0; i < dim; i++)
            {
                sum[i] += v[i] * (float)weight;
            }

            contributed = true;
        }

        if (!contributed)
        {
            return null;
        }

        NormalizeInPlace(sum);
        return sum;
    }

    /// <summary>Packs a float vector into little-endian bytes for BLOB storage.</summary>
    public static byte[] ToBlob(float[] vec) => MemoryMarshal.AsBytes(vec.AsSpan()).ToArray();

    /// <summary>Reads a float vector back from its BLOB form; null if the byte count isn't a whole number of floats.</summary>
    public static float[]? FromBlob(byte[] blob)
    {
        if (blob.Length == 0 || blob.Length % sizeof(float) != 0)
        {
            return null;
        }

        var vec = new float[blob.Length / sizeof(float)];
        Buffer.BlockCopy(blob, 0, vec, 0, blob.Length);
        return vec;
    }

    /// <summary>
    /// Weights for the hybrid score. Semantic similarity leads; genre/tag/author/quality
    /// refine and keep it grounded; obscurity biases toward mainstream or hidden gems. Tunable.
    /// </summary>
    public sealed record Weights(
        double Semantic = 3.0,
        double Genre = 1.0,
        double Tag = 1.5,
        double Author = 0.75,
        double Quality = 0.5,
        double Obscurity = 4.0);

    /// <summary>
    /// Combines the semantic cosine with the structured signals into a single rank score.
    /// <paramref name="cosine"/> is the seed↔candidate similarity; <paramref name="genreSum"/>
    /// is the summed seed-profile weight of the candidate's matched genres;
    /// <paramref name="tagScore"/> is the weighted-tag cosine ∈ [0,1] (<see cref="TagMath.Score"/>).
    /// <paramref name="obscuritySlider"/> ∈ [-1,1] (−1 mainstream … +1 hidden gems) times the
    /// candidate's popularity <paramref name="percentile"/> ∈ [0,1] (0 = most popular).
    /// </summary>
    public static double HybridScore(
        double cosine, double genreSum, double tagScore, bool authorMatch, double rating0To100,
        double obscuritySlider, double percentile, Weights w) =>
        (w.Semantic * cosine)
        + (w.Genre * genreSum)
        + (w.Tag * tagScore)
        + (authorMatch ? w.Author : 0)
        + (w.Quality * (rating0To100 / 100.0))
        + (w.Obscurity * obscuritySlider * (percentile - 0.5));
}
