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
    /// Levels either side of zero in a packed element. Four bits hold -8..7; the codec is symmetric
    /// like <see cref="Quantize"/>, so -8 is left unused rather than giving the negative side a
    /// step the positive side has no answer for.
    /// </summary>
    public const int PackedLevels = 7;

    /// <summary>
    /// Narrowest vector that is packed. Below it rows are stored one int8 per byte, unchanged.
    ///
    /// <para>
    /// Packing is only free because the per-element error averages out across the dot product, so
    /// it gets safer as vectors get wider and the floor is the honest expression of that. Measured:
    /// free at 768 (the only width that ships), and at 128 — the behavioural layer — the same
    /// change cost -0.0015 nDCG@40, 95% [-0.0032, +0.0001], which is not a result but is not
    /// nothing either. Nothing between those two widths has been measured, so this sits well above
    /// the one that looked borderline rather than splitting the difference.
    /// </para>
    /// </summary>
    public const int PackMinimumDimensions = 512;

    /// <summary>Whether a row this wide is stored as 4-bit levels rather than int8.</summary>
    public static bool ShouldPack(int dimensions) => dimensions >= PackMinimumDimensions;

    /// <summary>Bytes a stored row of <paramref name="dimensions"/> elements occupies.</summary>
    public static int PackedStride(int dimensions) =>
        ShouldPack(dimensions) ? (dimensions + 1) / 2 : dimensions;

    /// <summary>
    /// Packs an int8 row into 4-bit levels, two to a byte, and returns the factor the row's scale
    /// has to be multiplied by for a level to still stand for the same number.
    ///
    /// <para>
    /// Halves what the index holds — the packed vectors are the single largest allocation in the
    /// process, 93 MB against 46 MB at catalogue scale — and measured free: over 500 held-out
    /// reader libraries with the behavioural channel off, nDCG@40 moved +0.0010, bootstrap 95%
    /// [-0.0009, +0.0030], and over 400 requests on the independent MangaUpdates grader -0.0003,
    /// 95% [-0.0011, +0.0004]. Opposite signs and tight intervals, which is what no effect looks
    /// like. 768 dimensions is what makes it free: the per-element error averages out of a dot
    /// product that wide. Do NOT extend this to the behavioural vectors, which are 128 and where
    /// the same change measured -0.0015, 95% [-0.0032, +0.0001].
    /// </para>
    ///
    /// <para>
    /// An odd final element packs into the low nibble of the last byte and leaves the high one
    /// zero, which unpacks as a zero contribution rather than reading past the row.
    /// </para>
    /// </summary>
    public static float PackQuantized(ReadOnlySpan<sbyte> row, Span<byte> dest)
    {
        if (!ShouldPack(row.Length))
        {
            MemoryMarshal.AsBytes(row).CopyTo(dest);
            return 1f;
        }

        // Byte i holds element i in the low nibble and element i + dest.Length in the high one,
        // rather than the adjacent pair. Both halves of an unpacked row are then contiguous runs of
        // one nibble each, which is what lets UnpackQuantized do it in vector lanes instead of
        // interleaved scalar writes.
        var step = 127f / PackedLevels;
        for (var i = 0; i < dest.Length; i++)
        {
            var low = Level(row[i], step);
            var high = i + dest.Length < row.Length ? Level(row[i + dest.Length], step) : 0;
            dest[i] = (byte)((low & 0x0F) | (high << 4));
        }

        return step;

        static int Level(sbyte value, float step) =>
            Math.Clamp((int)MathF.Round(value / step), -PackedLevels, PackedLevels);
    }

    /// <summary>
    /// Expands a packed row into one sbyte per dimension. The values are LEVELS, not the int8s they
    /// came from, so they are only meaningful against a scale that has been through
    /// <see cref="PackQuantized"/>.
    /// </summary>
    public static void UnpackQuantized(ReadOnlySpan<byte> packed, Span<sbyte> dest)
    {
        if (!ShouldPack(dest.Length))
        {
            packed.CopyTo(MemoryMarshal.AsBytes(dest));
            return;
        }

        var half = packed.Length;
        var signed = MemoryMarshal.Cast<byte, sbyte>(packed);
        var i = 0;
        var width = Vector<sbyte>.Count;
        if (Vector.IsHardwareAccelerated && packed.Length >= width)
        {
            // Shifting left then arithmetic-right is what sign-extends a nibble; the high half only
            // needs the arithmetic shift. Both halves are whole lanes because of the layout
            // PackQuantized writes.
            for (; i <= packed.Length - width && i + half + width <= dest.Length; i += width)
            {
                var v = new Vector<sbyte>(signed.Slice(i, width));
                Vector.ShiftRightArithmetic(Vector.ShiftLeft(v, 4), 4).CopyTo(dest.Slice(i, width));
                Vector.ShiftRightArithmetic(v, 4).CopyTo(dest.Slice(i + half, width));
            }
        }

        for (; i < packed.Length; i++)
        {
            var b = packed[i];
            // Through sbyte in both halves: an int shift would not sign-extend a nibble, and every
            // negative level would read back as a large positive one.
            dest[i] = (sbyte)((sbyte)(b << 4) >> 4);
            if (i + half < dest.Length)
            {
                dest[i + half] = (sbyte)((sbyte)b >> 4);
            }
        }
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
    /// <para>
    /// <c>Tag</c> is 2.0, and <strong>it only means anything paired with
    /// <c>RecommenderTuning.TagCandidateNormPower</c></strong>, which is 0.75: damping the
    /// candidate norm roughly doubles a typical tag score, so this coefficient is a scale correction
    /// as much as a weight. Swept as a grid, 0.75/2.0 beat the undamped baseline by +0.0115 nDCG@40
    /// (95% [+0.0076, +0.0155]) while 0.75 with the weight left at 4.5 was indistinguishable and
    /// 0.5 with it left at 4.5 was worse. Change either one and re-sweep both; a value carried over
    /// from a different norm power is not a measurement of anything.
    /// </para>
    /// <para>
    /// The route here was 1.5 to 4.5 at norm power 1.0 (+0.0078, 95% [+0.0048, +0.0109]) and then
    /// back down once the norm was damped. The tag channel matters more than its coefficient
    /// suggests because premise lives in the tags (<c>Cohabitation</c>, <c>Arranged Marriage</c>)
    /// while the description embedding mostly carries tone and genre - though note that raising this
    /// alone does <em>not</em> buy premise specificity, since <see cref="TagMath.Score"/> scales
    /// signal and noise together. See <c>RecommenderTuning.TagProfileSharpening</c> for what failed.
    /// </para>
    /// <para>
    /// <c>Distinct</c> rewards a candidate one seed explains far better than the library as a whole
    /// does, measured as the gap between the best seed query's credit and the centroid's. It is the
    /// ranking half of what <c>RecommenderTuning.AttributionMargin</c> gates the label on: the margin
    /// decides which picks may say "feels like X", this decides how many such picks are on the page
    /// to begin with. Defaults to 0, which is the shipped behaviour of ranking without regard to
    /// whether any single seed stands behind a row. Its units follow
    /// <c>RecommenderTuning.QueryAttribution</c> - raw cosine difference under <c>RawCosine</c>,
    /// standard deviations once the channels are measured - so it has to be re-swept, not carried
    /// across, when that changes.
    /// </para>
    /// <para>
    /// It stays 0 against two fits that both asked for it. Both the pair-label and the reader-label
    /// regressions put a positive coefficient here, and paying it costs relevance: 0.3 measures
    /// neutral on held-out readers, 0.6 measures -0.0259, and franchise duplicates go 15% to 35%
    /// across that range. The fits are seeing something real and describing it wrong - a row only
    /// one seed explains is usually a row that seed already explained, which is a sequel.
    /// </para>
    /// <para>
    /// <c>Graph</c> and <c>CoRead</c> both default to 0 so every existing caller keeps its current
    /// behaviour: the two crowd channels are opt-in per call site, and <c>SemanticRecommender</c>
    /// is the only one that opts in (from <c>RecoGraphTuning.Weight</c> and
    /// <c>CoReadTuning.Weight</c>). It sets each one only when that graph actually returned
    /// evidence, so a missing artifact costs nothing.
    /// </para>
    /// </summary>
    /// <summary>
    /// One candidate's channel values, before <see cref="HybridScore"/> collapses them into a
    /// number. Collected only when a caller asks for it, which in practice means
    /// <c>distribution/fit-weights.cs</c>: fitting the coefficients needs the terms unblended, and
    /// recomputing them outside the scorer would be a second copy of every channel.
    /// </summary>
    /// <param name="Percentile">
    /// Popularity percentile, 0 = most popular. Carried so a fit can see whether a coefficient it
    /// likes is really just fame, which is the failure every table in this codebase is read against.
    /// </param>
    public readonly record struct CandidateFeatures(
        long Id,
        double Semantic,
        double Genre,
        double Tag,
        double Author,
        double Quality,
        double Graph,
        double CoRead,
        double Taste,
        double Distinct,
        double Percentile);

    public sealed record Weights(
        double Semantic = 3.0,
        double Genre = 1.0,
        double Tag = 2.0,
        double Author = 0.75,
        double Quality = 0.5,
        double Obscurity = 4.0,
        double Graph = 0.0,
        double CoRead = 0.0,
        double Distinct = 0.0,
        double Taste = 0.0);

    /// <summary>
    /// Combines the semantic cosine with the structured signals into a single rank score.
    /// <paramref name="cosine"/> is the seed↔candidate similarity; <paramref name="genreSum"/>
    /// is the summed seed-profile weight of the candidate's matched genres;
    /// <paramref name="tagScore"/> is the weighted-tag cosine ∈ [0,1] (<see cref="TagMath.Score"/>).
    /// <paramref name="obscuritySlider"/> ∈ [-1,1] (−1 mainstream … +1 hidden gems) times the
    /// candidate's popularity <paramref name="percentile"/> ∈ [0,1] (0 = most popular).
    /// <paramref name="graphScore"/> ∈ [0,1] is the co-recommendation evidence
    /// (<see cref="RecoGraph.RecoGraphScorer"/>); 0 means nobody ever paired this candidate with
    /// anything in the seed set, which is the common case and must cost the candidate nothing.
    /// <paramref name="tasteCosine"/> is the BEHAVIOURAL similarity: the cosine between the seed
    /// set's and the candidate's position in a space factorized out of real reading lists
    /// (<c>Maki.Metadata.Taste</c>). It is the only channel here that is neither what a series says
    /// about itself nor a lookup of pairs somebody was observed to share, which is why it reaches
    /// rows the crowd graphs are empty for. 0 means the artifact carries no vector for this row and
    /// must cost it nothing, exactly as with the two graph terms.
    /// <paramref name="coReadScore"/> ∈ [0,1] is the same for co-<em>reading</em>
    /// (<see cref="CoRead.CoReadScorer"/>) — what readers finished alongside the seeds rather than
    /// what they wrote recommendations about.
    /// <para>
    /// The two crowd terms are added, not combined or maxed. They answer different questions and
    /// disagree often: measured on the pilot matrix, only 27% of pairs covered by both graphs
    /// appear in both. Agreement is therefore genuine corroboration and worth paying for twice;
    /// folding them into one term would throw that away.
    /// </para>
    /// </summary>
    public static double HybridScore(
        double cosine, double genreSum, double tagScore, bool authorMatch, double rating0To100,
        double obscuritySlider, double percentile, Weights w,
        double graphScore = 0, double coReadScore = 0, double distinctiveness = 0,
        double tasteCosine = 0) =>
        (w.Semantic * cosine)
        + (w.Taste * tasteCosine)
        + (w.Genre * genreSum)
        + (w.Tag * tagScore)
        + (authorMatch ? w.Author : 0)
        + (w.Quality * (rating0To100 / 100.0))
        + (w.CoRead * coReadScore)
        + (w.Obscurity * obscuritySlider * (percentile - 0.5))
        + (w.Graph * graphScore)
        + (w.Distinct * distinctiveness);
}
