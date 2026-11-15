using System.Runtime.CompilerServices;
using Maki.Metadata.Embedding;

namespace Maki.Metadata.Taste;

/// <summary>
/// The implicit-ALS user solve against the fixed item vectors: the user factor the trainer itself
/// would fit for a seed set, <c>x = (YᵀY + α Σ w·y yᵀ + λI)⁻¹ (1 + α) Σ w·y</c>, over unit-normalized
/// item vectors. An alternative to the seed centroid, behind <see cref="TasteVectorTuning.UserSolve"/>.
/// </summary>
public static class TasteFoldIn
{
    private static readonly ConditionalWeakTable<VectorIndex, double[]> Grams = new();

    public static float[]? Solve(VectorIndex index, IReadOnlyList<(float[] Vec, double Weight)> seeds, double lambda, double alpha)
    {
        if (index.Taste is null || seeds.Count == 0)
        {
            return null;
        }

        var dim = index.Taste.Dimensions;
        var a = (double[])Grams.GetValue(index, Gram).Clone();
        var b = new double[dim];
        var unit = new double[dim];
        foreach (var (vec, weight) in seeds)
        {
            if (vec.Length != dim || weight <= 0 || !Normalize(vec, unit))
            {
                continue;
            }

            for (var i = 0; i < dim; i++)
            {
                b[i] += (1 + alpha) * weight * unit[i];
                for (var j = 0; j < dim; j++)
                {
                    a[(i * dim) + j] += alpha * weight * unit[i] * unit[j];
                }
            }
        }

        for (var i = 0; i < dim; i++)
        {
            a[(i * dim) + i] += lambda;
        }

        if (!CholeskySolve(a, b, dim))
        {
            return null;
        }

        var x = new float[dim];
        for (var i = 0; i < dim; i++)
        {
            x[i] = (float)b[i];
        }

        return x;
    }

    private static double[] Gram(VectorIndex index)
    {
        var dim = index.Taste!.Dimensions;
        var gram = new double[dim * dim];
        var unit = new double[dim];
        for (var row = 0; row < index.Count; row++)
        {
            if (index.TasteVectorAt(row) is not { } vec || !Normalize(vec, unit))
            {
                continue;
            }

            for (var i = 0; i < dim; i++)
            {
                var ui = unit[i];
                for (var j = i; j < dim; j++)
                {
                    gram[(i * dim) + j] += ui * unit[j];
                }
            }
        }

        for (var i = 0; i < dim; i++)
        {
            for (var j = 0; j < i; j++)
            {
                gram[(i * dim) + j] = gram[(j * dim) + i];
            }
        }

        return gram;
    }

    private static bool Normalize(float[] vec, double[] into)
    {
        var norm = 0.0;
        foreach (var v in vec)
        {
            norm += (double)v * v;
        }

        if (norm <= 0)
        {
            return false;
        }

        norm = Math.Sqrt(norm);
        for (var i = 0; i < vec.Length; i++)
        {
            into[i] = vec[i] / norm;
        }

        return true;
    }

    /// <summary>Solves <c>A x = b</c> in place for a symmetric positive definite <c>A</c>; x lands in b.</summary>
    internal static bool CholeskySolve(double[] a, double[] b, int n)
    {
        for (var j = 0; j < n; j++)
        {
            var d = a[(j * n) + j];
            for (var k = 0; k < j; k++)
            {
                d -= a[(j * n) + k] * a[(j * n) + k];
            }

            if (d <= 0)
            {
                return false;
            }

            d = Math.Sqrt(d);
            a[(j * n) + j] = d;
            for (var i = j + 1; i < n; i++)
            {
                var s = a[(i * n) + j];
                for (var k = 0; k < j; k++)
                {
                    s -= a[(i * n) + k] * a[(j * n) + k];
                }

                a[(i * n) + j] = s / d;
            }
        }

        for (var i = 0; i < n; i++)
        {
            var s = b[i];
            for (var k = 0; k < i; k++)
            {
                s -= a[(i * n) + k] * b[k];
            }

            b[i] = s / a[(i * n) + i];
        }

        for (var i = n - 1; i >= 0; i--)
        {
            var s = b[i];
            for (var k = i + 1; k < n; k++)
            {
                s -= a[(k * n) + i] * b[k];
            }

            b[i] = s / a[(i * n) + i];
        }

        return true;
    }
}
