using Maki.Core.Images;

namespace Maki.Core.Tests;

/// <summary>
/// <see cref="CreditDetector.Close"/> rejects by bounds and sums with vectors; it has to agree with the
/// plain loop on every pair. Rows are built the way strip rows are: multiples of 1/16 below 256.
/// </summary>
public class CreditRowCompareTests
{
    private const int W = CreditDetector.Width;
    private const float Limit = 12f * W;

    private static bool Reference(float[] a, float[] b)
    {
        var sum = 0f;
        for (var x = 0; x < W && sum < Limit; x++)
        {
            sum += MathF.Abs(a[x] - b[x]);
        }

        return sum < Limit;
    }

    private static bool Fast(float[] a, float[] b)
    {
        var (blocksA, blocksB) = (new float[CreditDetector.Blocks], new float[CreditDetector.Blocks]);
        var sumA = CreditDetector.RowSums(a, blocksA);
        var sumB = CreditDetector.RowSums(b, blocksB);
        return CreditDetector.Close(a, sumA, blocksA, b, sumB, blocksB);
    }

    private static float Sixteenth(Random random, int max = 255 * 16) => random.Next(0, max + 1) / 16f;

    private static float[] Row(Random random)
    {
        var row = new float[W];
        for (var x = 0; x < W; x++)
        {
            row[x] = Sixteenth(random);
        }

        return row;
    }

    /// <summary>A copy of <paramref name="row"/> moved so its summed difference lands near the limit.</summary>
    private static float[] Near(Random random, float[] row)
    {
        var left = Limit + random.Next(-64, 65) / 16f;
        var other = (float[])row.Clone();
        foreach (var x in Enumerable.Range(0, W).OrderBy(_ => random.Next()))
        {
            var step = MathF.Min(left, random.Next(0, 40 * 16) / 16f);
            other[x] = row[x] + step <= 255 ? row[x] + step : row[x] - step;
            left -= step;
        }

        return other;
    }

    [Fact]
    public void Matches_the_scalar_loop_on_random_rows()
    {
        var random = new Random(7);
        for (var n = 0; n < 20_000; n++)
        {
            var a = Row(random);
            var b = random.Next(3) == 0 ? Row(random) : a.Select(v => Math.Clamp(v + random.Next(-300, 301) / 16f, 0, 255)).ToArray();
            Assert.Equal(Reference(a, b), Fast(a, b));
        }
    }

    [Fact]
    public void Matches_the_scalar_loop_near_the_limit()
    {
        var random = new Random(11);
        var (accepted, rejected) = (0, 0);
        for (var n = 0; n < 20_000; n++)
        {
            var a = Row(random);
            var b = Near(random, a);
            var expected = Reference(a, b);
            Assert.Equal(expected, Fast(a, b));
            if (expected)
            {
                accepted++;
            }
            else
            {
                rejected++;
            }
        }

        Assert.True(accepted > 1000 && rejected > 1000, $"{accepted} accepted, {rejected} rejected");
    }

    [Fact]
    public void Matches_the_scalar_loop_on_flat_rows_that_differ_in_level()
    {
        for (var level = 0; level < 40 * 16; level++)
        {
            var a = Enumerable.Repeat(100f, W).ToArray();
            var b = Enumerable.Repeat(100f + level / 16f, W).ToArray();
            Assert.Equal(Reference(a, b), Fast(a, b));
        }
    }
}
