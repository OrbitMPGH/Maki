using Maki.Core.Images.Upscaling;

namespace Maki.Core.Tests;

/// <summary>
/// <see cref="UpscalePixels.TakeColourFrom"/> works band by band to keep its peak down. It must give
/// byte for byte what the whole-page colour pass gave before, kept here as
/// <see cref="WholePage"/>.
/// </summary>
public class UpscalePixelsBandTests
{
    [Theory]
    [InlineData(37, 5, 2, 1)]
    [InlineData(64, 33, 2, 1)]
    [InlineData(40, 64, 2, 1)]
    [InlineData(40, 65, 2, 1)]
    [InlineData(50, 200, 2, 3)]
    [InlineData(31, 97, 1, 2)]
    [InlineData(17, 70, 4, 7)]
    [InlineData(80, 1000, 2, 4)]
    [InlineData(23, 400, 2, 18)]
    public void Bands_give_the_bytes_the_whole_page_pass_gave(int sourceWidth, int sourceHeight, int scale, int threads)
    {
        var (source, output) = Page(sourceWidth, sourceHeight, scale, seed: sourceWidth * 31 + sourceHeight);
        var expected = (byte[])output.Clone();
        WholePage(expected, sourceWidth * scale, sourceHeight * scale, source, sourceWidth, sourceHeight);

        UpscalePixels.TakeColourFrom(output, sourceWidth * scale, sourceHeight * scale, source, sourceWidth, sourceHeight, threads);

        Assert.Equal(expected, output);
    }

    /// <summary>A colour page with soft gradients, flat patches a few levels apart, saturated blocks and noise.</summary>
    private static (byte[] Source, byte[] Output) Page(int width, int height, int scale, int seed)
    {
        var random = new Random(seed);
        var source = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var p = (y * width + x) * 3;
            var block = (x / 8 + y / 8) % 5;
            var (r, g, b) = block switch
            {
                0 => (40 + random.Next(4), 20, 60 + random.Next(4)),
                1 => (200, 30 + random.Next(3), 30),
                2 => (x * 3 % 256, y * 2 % 256, (x + y) % 256),
                3 => (128, 128, 128),
                _ => (random.Next(256), random.Next(256), random.Next(256)),
            };
            (source[p], source[p + 1], source[p + 2]) = ((byte)r, (byte)g, (byte)b);
        }

        var output = new byte[width * scale * height * scale * 3];
        for (var y = 0; y < height * scale; y++)
        for (var x = 0; x < width * scale; x++)
        {
            var o = (y * width * scale + x) * 3;
            var s = ((y / scale) * width + x / scale) * 3;
            for (var c = 0; c < 3; c++)
            {
                output[o + c] = (byte)Math.Clamp(source[s + c] + random.Next(-6, 7), 0, 255);
            }
        }

        return (source, output);
    }

    private const int SmoothReach = 2;
    private const int SmoothStep = 2;
    private const double SmoothSpace = 4;
    private const double SmoothLuma = 4;
    private const double SmoothChroma = 3;
    private const int ChromaQuantum = 4;
    private const int ChromaBins = 4;

    private static readonly float[] SmoothChromaWeights = Enumerable.Range(0, (int)(9 * 2 * SmoothChroma * SmoothChroma) * ChromaBins)
        .Select(i => (float)Math.Exp(-(double)i / ChromaBins / (2 * SmoothChroma * SmoothChroma))).ToArray();

    private static readonly float[] SmoothTapWeights = Enumerable.Range(0, (2 * SmoothReach + 1) * (2 * SmoothReach + 1) * 256)
        .Select(i =>
        {
            var tap = i / 256;
            var dy = (tap / (2 * SmoothReach + 1) - SmoothReach) * SmoothStep;
            var dx = (tap % (2 * SmoothReach + 1) - SmoothReach) * SmoothStep;
            var dl = i % 256;
            return (float)Math.Exp(-(dx * dx + dy * dy) / (2 * SmoothSpace * SmoothSpace) - dl * dl / (2 * SmoothLuma * SmoothLuma));
        }).ToArray();

    /// <summary>The colour pass as it was before it went block by block, single-threaded.</summary>
    private static void WholePage(byte[] output, int width, int height, byte[] source, int sourceWidth, int sourceHeight)
    {
        var scale = width / sourceWidth;
        var pixels = sourceWidth * sourceHeight;
        var guide = new byte[pixels];
        var chroma = new short[pixels * 2];
        for (var y = 0; y < sourceHeight; y++)
        for (var x = 0; x < sourceWidth; x++)
        {
            var sum = 0;
            for (var dy = 0; dy < scale; dy++)
            {
                var o = ((y * scale + dy) * width + x * scale) * 3;
                for (var dx = 0; dx < scale; dx++, o += 3)
                {
                    sum += UpscalePixels.Luma(output[o], output[o + 1], output[o + 2]);
                }
            }

            var p = y * sourceWidth + x;
            guide[p] = (byte)((sum + scale * scale / 2) / (scale * scale));
            int r = source[p * 3], g = source[p * 3 + 1], b = source[p * 3 + 2];
            var l = UpscalePixels.Luma(r, g, b);
            chroma[p * 2] = (short)MathF.Round(0.564f * (b - l) * ChromaQuantum);
            chroma[p * 2 + 1] = (short)MathF.Round(0.713f * (r - l) * ChromaQuantum);
        }

        var smooth = new float[pixels * 2];
        var side = 2 * SmoothReach + 1;
        var binShift = (int)Math.Log2(ChromaQuantum * ChromaQuantum / ChromaBins);
        var rows = new int[side];
        var cols = new int[side];
        for (var y = 0; y < sourceHeight; y++)
        {
            for (var t = 0; t < side; t++)
            {
                rows[t] = Math.Clamp(y + (t - SmoothReach) * SmoothStep, 0, sourceHeight - 1) * sourceWidth;
            }

            for (var x = 0; x < sourceWidth; x++)
            {
                var p = y * sourceWidth + x;
                for (var t = 0; t < side; t++)
                {
                    cols[t] = Math.Clamp(x + (t - SmoothReach) * SmoothStep, 0, sourceWidth - 1);
                }

                int cb0 = chroma[p * 2], cr0 = chroma[p * 2 + 1], y0 = guide[p];
                float sumCb = 0, sumCr = 0, sumW = 0;
                for (var ty = 0; ty < side; ty++)
                for (var tx = 0; tx < side; tx++)
                {
                    var n = rows[ty] + cols[tx];
                    int cb = chroma[n * 2], cr = chroma[n * 2 + 1];
                    var bin = ((cb - cb0) * (cb - cb0) + (cr - cr0) * (cr - cr0)) >> binShift;
                    if (bin >= SmoothChromaWeights.Length)
                    {
                        continue;
                    }

                    var w = SmoothTapWeights[(ty * side + tx) * 256 + Math.Abs(guide[n] - y0)] * SmoothChromaWeights[bin];
                    sumCb += w * cb;
                    sumCr += w * cr;
                    sumW += w;
                }

                smooth[p * 2] = sumCb / sumW / (ChromaQuantum * 0.564f);
                smooth[p * 2 + 1] = sumCr / sumW / (ChromaQuantum * 0.713f);
            }
        }

        for (var y = 0; y < height; y++)
        {
            var sy = Math.Clamp((y + 0.5f) / scale - 0.5f, 0, sourceHeight - 1);
            var y0 = (int)sy;
            var y1 = Math.Min(y0 + 1, sourceHeight - 1);
            var fy = sy - y0;
            var top = y0 * sourceWidth * 2;
            var bottom = y1 * sourceWidth * 2;
            for (var x = 0; x < width; x++)
            {
                var sx = Math.Clamp((x + 0.5f) / scale - 0.5f, 0, sourceWidth - 1);
                var x0 = (int)sx;
                var x1 = Math.Min(x0 + 1, sourceWidth - 1);
                var fx = sx - x0;
                var db = Bilinear(smooth, top + x0 * 2, top + x1 * 2, bottom + x0 * 2, bottom + x1 * 2, fx, fy);
                var dr = Bilinear(smooth, top + x0 * 2 + 1, top + x1 * 2 + 1, bottom + x0 * 2 + 1, bottom + x1 * 2 + 1, fx, fy);
                var dg = -(0.299f * dr + 0.114f * db) / 0.587f;
                var o = (y * width + x) * 3;
                var l = UpscalePixels.Luma(output[o], output[o + 1], output[o + 2]);
                output[o] = (byte)Math.Clamp((int)MathF.Round(l + dr), 0, 255);
                output[o + 1] = (byte)Math.Clamp((int)MathF.Round(l + dg), 0, 255);
                output[o + 2] = (byte)Math.Clamp((int)MathF.Round(l + db), 0, 255);
            }
        }
    }

    private static float Bilinear(float[] v, int a, int b, int c, int d, float fx, float fy) =>
        (v[a] * (1 - fx) + v[b] * fx) * (1 - fy) + (v[c] * (1 - fx) + v[d] * fx) * fy;
}
