using System.Numerics;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;

namespace Maki.Metadata.Embedding;

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

    /// <summary>
    /// Quantizes a vector to int8 with a per-vector scale, for the in-memory search index
    /// (<see cref="VectorIndex"/>) — a quarter of float32's memory, and on unit vectors the
    /// cosine error stays well under the gap between adjacent search results. Returns the scale
    /// the integer dot product has to be multiplied by; <paramref name="dest"/> must be at least
    /// as long as <paramref name="vec"/>.
    /// </summary>
    public static float Quantize(ReadOnlySpan<float> vec, Span<sbyte> dest)
    {
        var max = MathF.Abs(TensorPrimitives.MaxMagnitude(vec));
        if (max <= 1e-8f)
        {
            dest[..vec.Length].Clear();
            return 0f;
        }

        var scale = max / 127f;
        for (var i = 0; i < vec.Length; i++)
        {
            dest[i] = (sbyte)Math.Clamp((int)MathF.Round(vec[i] / scale), -127, 127);
        }

        return scale;
    }

    /// <summary>
    /// Dot product of two <see cref="Quantize"/>d vectors, staying in integers the whole way.
    /// Both are unit-normalized, so this is the cosine.
    /// <para>
    /// The query is quantized once per search (<see cref="QuantizeQuery"/>) so a scan never widens
    /// a row back to float: the previous shape converted each row into a 768-float scratch buffer
    /// and dotted that, which is ~6 KB of L1 traffic per row on top of the 768 bytes the row
    /// actually is. Quantizing the query too roughly doubles the cosine's error — still ~3 decimal
    /// digits on unit vectors, far finer than the gap between neighbouring results, which is the
    /// same trade the stored rows already make.
    /// </para>
    /// </summary>
    public static float QuantizedDot(
        ReadOnlySpan<sbyte> query, float queryScale, ReadOnlySpan<sbyte> row, float rowScale) =>
        query.Length == row.Length ? IntegerDot(query, row) * queryScale * rowScale : 0f;

    /// <summary>
    /// Quantizes a query vector so it can be dotted against stored rows without leaving integers.
    /// Same codec as <see cref="Quantize"/>; separate name because the lifetime is different — a
    /// query is packed once and reused across every row of a scan.
    /// </summary>
    public static sbyte[] QuantizeQuery(ReadOnlySpan<float> query, out float scale)
    {
        var packed = new sbyte[query.Length];
        scale = Quantize(query, packed);
        return packed;
    }

    /// <summary>
    /// Sum of elementwise int8 products, accumulated in int32. A product peaks at 127×127, so even
    /// a 1024-dimension vector cannot come near overflowing — which is what lets the whole dot stay
    /// in integer lanes, four to a float's width.
    /// </summary>
    private static int IntegerDot(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> b)
    {
        var sum = 0;
        var i = 0;
        var width = Vector<sbyte>.Count;

        if (Vector.IsHardwareAccelerated && a.Length >= width)
        {
            var acc = Vector<int>.Zero;
            for (; i <= a.Length - width; i += width)
            {
                Vector.Widen(new Vector<sbyte>(a.Slice(i, width)), out var aShortLow, out var aShortHigh);
                Vector.Widen(new Vector<sbyte>(b.Slice(i, width)), out var bShortLow, out var bShortHigh);
                Vector.Widen(aShortLow, out var a0, out var a1);
                Vector.Widen(aShortHigh, out var a2, out var a3);
                Vector.Widen(bShortLow, out var b0, out var b1);
                Vector.Widen(bShortHigh, out var b2, out var b3);
                acc += (a0 * b0) + (a1 * b1) + (a2 * b2) + (a3 * b3);
            }

            sum = Vector.Sum(acc);
        }

        for (; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    /// <summary>
    /// Maximal Marginal Relevance: picks <paramref name="take"/> candidates that are individually
    /// relevant but not near-copies of each other, which is what stops a recommendation page from
    /// being eight volumes of the same shelf.
    /// <para>
    /// Candidates are referred to by index into <paramref name="relevance"/>, which must already be
    /// normalized to [0,1] — MMR subtracts a similarity (also [0,1]) from it, so an unbounded score
    /// on one side would make <paramref name="lambda"/> mean nothing. <paramref name="lambda"/> is
    /// the diversity weight: 0 returns the plain relevance order (so it is a safe default — the
    /// feature is inert until somebody asks for it), 1 ignores relevance after the first pick.
    /// </para>
    /// <para>
    /// The running "how close is this to anything already picked" is carried forward rather than
    /// recomputed, so the cost is one similarity call per candidate per pick.
    /// </para>
    /// </summary>
    public static IReadOnlyList<int> SelectDiverse(
        IReadOnlyList<double> relevance, Func<int, int, double> similarity, int take, double lambda)
    {
        take = Math.Min(take, relevance.Count);
        if (take <= 0)
        {
            return [];
        }

        lambda = Math.Clamp(lambda, 0, 1);
        var order = Enumerable.Range(0, relevance.Count).OrderByDescending(i => relevance[i]).ToList();
        if (lambda <= 0)
        {
            return order.Take(take).ToList();
        }

        var picked = new List<int>(take) { order[0] };
        var remaining = order.Skip(1).ToList();
        var maxSimilarity = remaining.Select(c => similarity(c, picked[0])).ToList();

        while (picked.Count < take && remaining.Count > 0)
        {
            var best = 0;
            var bestValue = double.NegativeInfinity;
            for (var i = 0; i < remaining.Count; i++)
            {
                var value = ((1 - lambda) * relevance[remaining[i]]) - (lambda * maxSimilarity[i]);
                if (value > bestValue)
                {
                    bestValue = value;
                    best = i;
                }
            }

            var chosen = remaining[best];
            picked.Add(chosen);
            remaining.RemoveAt(best);
            maxSimilarity.RemoveAt(best);
            for (var i = 0; i < remaining.Count; i++)
            {
                maxSimilarity[i] = Math.Max(maxSimilarity[i], similarity(remaining[i], chosen));
            }
        }

        return picked;
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

    /// <summary>Reads an int8-quantized vector back from its BLOB form.</summary>
    public static float[]? FromQuantizedBlob(byte[] blob, float scale)
    {
        if (blob.Length == 0 || scale <= 0 || float.IsNaN(scale) || float.IsInfinity(scale))
        {
            return null;
        }

        var vec = new float[blob.Length];
        for (var i = 0; i < blob.Length; i++)
        {
            vec[i] = (sbyte)blob[i] * scale;
        }

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
