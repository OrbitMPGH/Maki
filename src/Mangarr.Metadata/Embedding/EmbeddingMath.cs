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
    /// refine and keep it grounded. Tunable — these are the Phase 1 defaults.
    /// </summary>
    public sealed record Weights(
        double Semantic = 3.0,
        double Genre = 1.0,
        double Tag = 0.5,
        double Author = 0.75,
        double Quality = 0.5);

    /// <summary>
    /// Combines the semantic cosine with the structured signals into a single rank score.
    /// <paramref name="cosine"/> is the seed↔candidate similarity; the *Sum params are the
    /// summed library-profile weights of the candidate's matched genres/tags.
    /// </summary>
    public static double HybridScore(
        double cosine, double genreSum, double tagSum, bool authorMatch, double rating0To100, Weights w) =>
        (w.Semantic * cosine)
        + (w.Genre * genreSum)
        + (w.Tag * tagSum)
        + (authorMatch ? w.Author : 0)
        + (w.Quality * (rating0To100 / 100.0));
}
