using Maki.Core.Images;

namespace Maki.Core.Tests;

/// <summary>Generated webtoon strips: noisy "art" with flat white gutters at the given rows.</summary>
public static class TallStrips
{
    public static Raster Strip(int width, int height, IReadOnlyList<(int Start, int End)> gutters, int seed = 1)
    {
        var raster = new Raster(width, height, bands: 3);
        var random = new Random(seed);
        var pixels = raster.Pixels;
        for (var y = 0; y < height; y++)
        {
            var row = pixels.AsSpan(y * raster.Stride, raster.Stride);
            if (gutters.Any(g => y >= g.Start && y < g.End))
            {
                row.Fill(255);
                continue;
            }

            for (var x = 0; x < row.Length; x += 3)
            {
                var tone = (byte)Math.Clamp((x / 3 + y) % 200 + random.Next(40), 0, 255);
                row[x] = tone;
                row[x + 1] = (byte)(255 - tone);
                row[x + 2] = tone;
            }
        }

        return raster;
    }

    public static byte[] Jpeg(int width, int height, IReadOnlyList<(int Start, int End)> gutters, int quality = 97) =>
        Strip(width, height, gutters).EncodeJpeg(quality);

    /// <summary>Rows of one deviation, with flat rows (deviation 0) at the given ranges.</summary>
    public static double[] Rows(int height, params (int Start, int End)[] flat)
    {
        var rows = Enumerable.Repeat(40.0, height).ToArray();
        foreach (var (start, end) in flat)
        {
            for (var y = start; y < end; y++) rows[y] = 0;
        }

        return rows;
    }
}
