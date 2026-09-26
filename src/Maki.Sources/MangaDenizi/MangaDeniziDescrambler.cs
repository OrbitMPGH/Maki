using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Maki.Sources.MangaDenizi;

/// <summary>
/// Reverses MangaDenizi's "tiled-v1" page scramble. The image is cut into an l x l grid
/// (l = min(grid, min(width, height))) and each axis is independently permuted by an
/// xorshift32 PRNG seeded from the page's own seed. Ported from Keiyoushi's
/// UnscramblerInterceptor.kt; every intermediate value is unsigned 32-bit, matching the
/// original Kotlin UInt arithmetic exactly (including the wraparound on shifts).
/// </summary>
public static class MangaDeniziDescrambler
{
    private const uint Ta = 2463534242;
    private const uint Vo = 2654435769;
    private const uint Bo = 2246822507;

    public static Image<Rgb24> Descramble(Image<Rgb24> source, int grid, uint seed)
    {
        var width = source.Width;
        var height = source.Height;
        var l = Math.Max(1, Math.Min(grid, Math.Min(width, height)));

        var columns = Slices(width, l);
        var rows = Slices(height, l);
        var columnOrder = Shuffle(l, seed ^ Bo);
        var rowOrder = Shuffle(l, seed ^ Vo);
        var scrambledColumns = MapSlices(columns, columnOrder);
        var scrambledRows = MapSlices(rows, rowOrder);

        var result = new Image<Rgb24>(width, height);
        result.Mutate(ctx =>
        {
            for (var row = 0; row < l; row++)
            {
                for (var col = 0; col < l; col++)
                {
                    var sourceRect = new Rectangle(
                        scrambledColumns[col].Offset,
                        scrambledRows[row].Offset,
                        scrambledColumns[col].Length,
                        scrambledRows[row].Length);
                    var destination = new Point(columns[columnOrder[col]].Offset, rows[rowOrder[row]].Offset);
                    ctx.DrawImage(source, destination, sourceRect, 1f);
                }
            }
        });

        return result;
    }

    private static uint Next(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    /// <summary>Fisher-Yates shuffle of 0..n-1 driven by the xorshift32 sequence seeded from <paramref name="seed"/>.</summary>
    internal static int[] Shuffle(int n, uint seed)
    {
        var count = Math.Max(1, n);
        var order = new int[count];
        for (var i = 0; i < count; i++)
        {
            order[i] = i;
        }

        var state = seed == 0 ? Ta : seed;
        for (var k = count - 1; k >= 1; k--)
        {
            var i = (int)(Next(ref state) % (uint)(k + 1));
            (order[k], order[i]) = (order[i], order[k]);
        }

        return order;
    }

    /// <summary>Splits <paramref name="total"/> into <paramref name="pieces"/> near-equal, gap-free slices.</summary>
    internal static Slice[] Slices(int total, int pieces)
    {
        var r = Math.Max(1, Math.Min(pieces, total));
        var slices = new Slice[r];
        for (var i = 0; i < r; i++)
        {
            var offset = i * total / r;
            var length = Math.Max(1, (i + 1) * total / r - offset);
            slices[i] = new Slice(offset, length);
        }

        return slices;
    }

    /// <summary>
    /// Lays <paramref name="slices"/> back to back in the order given by <paramref name="order"/>,
    /// producing the offset each slice sits at once the pieces are placed in that order.
    /// </summary>
    internal static Slice[] MapSlices(Slice[] slices, int[] order)
    {
        var mapped = new Slice[order.Length];
        var offset = 0;
        for (var i = 0; i < order.Length; i++)
        {
            var length = slices[order[i]].Length;
            mapped[i] = new Slice(offset, length);
            offset += length;
        }

        return mapped;
    }

    internal readonly record struct Slice(int Offset, int Length);
}
