namespace Maki.Api.Services;

/// <summary>
/// Spherical k-means over unit-length vectors, and the model selection that decides how many
/// groups a reader actually has.
///
/// <para>
/// Separate from <see cref="TasteInsightsService"/> and free of every Maki type so the arithmetic
/// can be tested on synthetic points: "does this split two obvious blobs" is a question about the
/// algorithm, not about anybody's library.
/// </para>
///
/// <para>
/// Spherical rather than plain k-means because the index's vectors are unit-normalized and every
/// other consumer scores them by cosine. On unit vectors a dot product is the cosine, so the
/// assignment step is a plain maximisation and the update step only has to renormalize.
/// </para>
/// </summary>
public static class TasteClustering
{
    /// <summary>
    /// Fewest points a group needs before it is a taste rather than a coincidence. Two series that
    /// happen to sit near each other is not a reading habit.
    /// </summary>
    public const int MinClusterSize = 3;

    /// <summary>
    /// And the smallest share of the library a group may hold.
    /// <para>
    /// Without this, k-means happily answers with one group holding nearly everything and a second
    /// holding four outliers, which scores well (peeling off the stragglers tightens the remainder)
    /// and tells the reader nothing: the big group is just "your library" again, and by definition
    /// has nothing that distinguishes it. A split has to actually divide.
    /// </para>
    /// </summary>
    private const double MinClusterShare = 0.08;

    /// <summary>
    /// Below this many points there is nothing to split: any division of five series says more
    /// about the algorithm than the reader.
    /// </summary>
    public const int MinPoints = 8;

    /// <summary>Most groups worth naming. Past this the labels stop being distinguishable.</summary>
    private const int MaxK = 5;

    /// <summary>
    /// How much mean cosine-to-own-centroid a split has to buy before the extra group is worth it.
    /// Without a floor, k always looks better one higher and every reader ends up with five groups.
    /// </summary>
    private const double MinGainPerCluster = 0.008;

    private const int MaxIterations = 40;

    /// <summary>
    /// Fixed, and deliberately not time- or hash-seeded. The page is cached for half an hour but
    /// recomputed after that, and a reader whose groups reshuffle between visits would reasonably
    /// conclude the feature is making it up.
    /// </summary>
    private const int Seed = 0x5EED;

    /// <param name="Assignments">Cluster index per input point, parallel to the input.</param>
    /// <param name="Centroids">Unit-length centroid per cluster.</param>
    public record Result(int[] Assignments, float[][] Centroids)
    {
        public int K => Centroids.Length;
    }

    /// <summary>
    /// Groups <paramref name="points"/> (which must be unit length) and picks the number of groups.
    /// Returns null when there is too little to say.
    /// </summary>
    public static Result? Cluster(IReadOnlyList<float[]> points)
    {
        if (points.Count < MinPoints)
        {
            return null;
        }

        // Prefer a split where every group is a real share of the library. Failing that, take one
        // that merely clears the hard size floor: a reader whose vectors genuinely do not divide
        // evenly is better served by an uneven answer than by being told there is nothing to say.
        return Best(points, requireShare: true) ?? Best(points, requireShare: false);
    }

    private static Result? Best(IReadOnlyList<float[]> points, bool requireShare)
    {
        Result? best = null;
        var bestScore = double.NegativeInfinity;

        for (var k = 2; k <= Math.Min(MaxK, points.Count / MinClusterSize); k++)
        {
            var candidate = Run(points, k, requireShare);
            if (candidate is null)
            {
                continue;
            }

            var score = MeanCosineToOwnCentroid(points, candidate);

            // Every extra group buys some tightness for free, so a bare improvement proves nothing.
            // Charge each one, and keep the larger k only when it clears the toll.
            var toll = (k - 2) * MinGainPerCluster;
            if (score - toll > bestScore)
            {
                bestScore = score - toll;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>One k-means run, or null when a group came back too small to be worth naming.</summary>
    private static Result? Run(IReadOnlyList<float[]> points, int k, bool requireShare)
    {
        var dimensions = points[0].Length;
        var centroids = InitialCentroids(points, k);
        var assignments = new int[points.Count];
        Array.Fill(assignments, -1);

        for (var iteration = 0; iteration < MaxIterations; iteration++)
        {
            var moved = false;
            for (var i = 0; i < points.Count; i++)
            {
                var nearest = Nearest(points[i], centroids);
                if (assignments[i] != nearest)
                {
                    assignments[i] = nearest;
                    moved = true;
                }
            }

            if (!moved)
            {
                break;
            }

            for (var c = 0; c < k; c++)
            {
                var sum = new float[dimensions];
                var count = 0;
                for (var i = 0; i < points.Count; i++)
                {
                    if (assignments[i] != c)
                    {
                        continue;
                    }

                    count++;
                    for (var d = 0; d < dimensions; d++)
                    {
                        sum[d] += points[i][d];
                    }
                }

                // An emptied centroid is left where it was rather than reseeded: reseeding turns a
                // deterministic run into one that depends on which point happened to be furthest,
                // and the size floor below rejects the run anyway.
                if (count > 0 && Normalize(sum))
                {
                    centroids[c] = sum;
                }
            }
        }

        var sizes = new int[k];
        foreach (var a in assignments)
        {
            sizes[a]++;
        }

        var floor = requireShare
            ? Math.Max(MinClusterSize, (int)Math.Ceiling(points.Count * MinClusterShare))
            : MinClusterSize;
        return sizes.Any(s => s < floor) ? null : new Result(assignments, centroids);
    }

    /// <summary>
    /// k-means++ seeding, walked deterministically. The first centre is the point furthest from the
    /// overall mean rather than a random one, so the whole run reproduces exactly.
    /// </summary>
    private static float[][] InitialCentroids(IReadOnlyList<float[]> points, int k)
    {
        var dimensions = points[0].Length;
        var mean = new float[dimensions];
        foreach (var p in points)
        {
            for (var d = 0; d < dimensions; d++)
            {
                mean[d] += p[d];
            }
        }

        Normalize(mean);

        var centroids = new List<float[]>(k);
        var firstIndex = 0;
        var worst = double.PositiveInfinity;
        for (var i = 0; i < points.Count; i++)
        {
            var similarity = Dot(points[i], mean);
            if (similarity < worst)
            {
                worst = similarity;
                firstIndex = i;
            }
        }

        centroids.Add((float[])points[firstIndex].Clone());

        var random = new Random(Seed);
        while (centroids.Count < k)
        {
            // Distance to the nearest chosen centre, as k-means++ wants, with cosine standing in
            // for squared distance: on unit vectors the two order points identically.
            var weights = new double[points.Count];
            var total = 0.0;
            for (var i = 0; i < points.Count; i++)
            {
                var nearest = centroids.Max(c => Dot(points[i], c));
                var distance = Math.Max(0, 1 - nearest);
                weights[i] = distance * distance;
                total += weights[i];
            }

            if (total <= 0)
            {
                break; // every point sits on a chosen centre; nothing left to seed with
            }

            var target = random.NextDouble() * total;
            var pick = points.Count - 1;
            for (var i = 0; i < points.Count; i++)
            {
                target -= weights[i];
                if (target <= 0)
                {
                    pick = i;
                    break;
                }
            }

            centroids.Add((float[])points[pick].Clone());
        }

        return [.. centroids];
    }

    private static int Nearest(float[] point, float[][] centroids)
    {
        var best = 0;
        var bestSimilarity = double.NegativeInfinity;
        for (var c = 0; c < centroids.Length; c++)
        {
            var similarity = Dot(point, centroids[c]);
            if (similarity > bestSimilarity)
            {
                bestSimilarity = similarity;
                best = c;
            }
        }

        return best;
    }

    private static double MeanCosineToOwnCentroid(IReadOnlyList<float[]> points, Result result)
    {
        var total = 0.0;
        for (var i = 0; i < points.Count; i++)
        {
            total += Dot(points[i], result.Centroids[result.Assignments[i]]);
        }

        return total / points.Count;
    }

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
