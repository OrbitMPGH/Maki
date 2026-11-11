namespace Maki.Core.Images.Upscaling;

public static class UpscalePixels
{
    private const int MaxSamples = 1 << 16;
    private const int ColourSpread = 16;
    private const int MaxMeanSpread = 6;

    /// <summary>
    /// True when interleaved 8-bit RGB is grey. Up to 65536 pixels are sampled, one at a jittered
    /// position in each equal run so the samples cannot line up with columns. The page is colour when
    /// more than 1 in 2000 samples has a channel spread (max minus min) over 16, so a small saturated
    /// title still counts, or when the mean spread is over 6, so an even tint such as sepia counts too.
    /// </summary>
    public static bool IsGreyscale(ReadOnlySpan<byte> rgb)
    {
        var pixels = rgb.Length / 3;
        if (pixels == 0)
        {
            return true;
        }

        var step = Math.Max(1, pixels / MaxSamples);
        var samples = (pixels + step - 1) / step;
        var allowed = samples / 2000;
        var colour = 0;
        long spreadSum = 0;
        var jitter = 0x9E3779B9u;
        for (var start = 0; start < pixels; start += step)
        {
            jitter ^= jitter << 13;
            jitter ^= jitter >> 17;
            jitter ^= jitter << 5;
            var p = Math.Min(pixels - 1, start + (int)(jitter % (uint)step));
            int r = rgb[p * 3], g = rgb[p * 3 + 1], b = rgb[p * 3 + 2];
            var spread = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
            spreadSum += spread;
            if (spread > ColourSpread && ++colour > allowed)
            {
                return false;
            }
        }

        return spreadSum <= (long)samples * MaxMeanSpread;
    }

    private const int SmoothReach = 2;     // taps each side: a 5 x 5 grid
    private const int SmoothStep = 2;      // source px between taps, so the grid spans an 8 px JPEG block
    private const double SmoothSpace = 4;  // source px
    private const double SmoothLuma = 4;   // luma levels
    private const double SmoothChroma = 3; // Cb/Cr levels
    private const int ChromaQuantum = 4;   // stored Cb/Cr steps per level
    private const int ChromaBins = 4;      // LUT bins per squared Cb/Cr level
    private const int SmoothBandRows = 32;

    private static readonly float[] SmoothChromaWeights = Enumerable.Range(0, (int)(9 * 2 * SmoothChroma * SmoothChroma) * ChromaBins)
        .Select(i => (float)Math.Exp(-(double)i / ChromaBins / (2 * SmoothChroma * SmoothChroma))).ToArray();

    /// <summary>Distance weight times luma weight, by tap and by luma difference.</summary>
    private static readonly float[] SmoothTapWeights = Enumerable.Range(0, (2 * SmoothReach + 1) * (2 * SmoothReach + 1) * 256)
        .Select(i =>
        {
            var tap = i / 256;
            var dy = (tap / (2 * SmoothReach + 1) - SmoothReach) * SmoothStep;
            var dx = (tap % (2 * SmoothReach + 1) - SmoothReach) * SmoothStep;
            var dl = i % 256;
            return (float)Math.Exp(-(dx * dx + dy * dy) / (2 * SmoothSpace * SmoothSpace) - dl * dl / (2 * SmoothLuma * SmoothLuma));
        }).ToArray();

    /// <summary>
    /// Keeps the brightness (Rec. 601 luma) of interleaved RGB <paramref name="output"/> and takes its
    /// colour from <paramref name="source"/>, the original page, whose size times a whole scale is the
    /// output's. The models tint flat screentone; with this a grey page comes out exactly grey and a
    /// colour page keeps its colour.
    ///
    /// <para>
    /// The source's colour is smoothed on the way, at the source's own size: each pixel takes a mean of
    /// its neighbours' colour, weighted by distance, by how close their brightness is in the model output
    /// (shrunk to the source's size) and by how close their colour already is. A JPEG's 8 px blocks each
    /// carry their own slight tint, which the grain hides in the source; once a model removes the grain
    /// they show as boxes, worst in dark fills on an OLED. Block steps are small and average out, while
    /// colour edges and line edges are large steps and stay. The colour is then sampled up bilinearly.
    /// </para>
    /// </summary>
    public static void TakeColourFrom(byte[] output, int width, int height, byte[] source, int sourceWidth, int sourceHeight,
        int threads)
    {
        var scale = sourceWidth > 0 ? width / sourceWidth : 0;
        if (scale < 1 || width != sourceWidth * scale || height != sourceHeight * scale
            || output.Length != width * height * 3 || source.Length != sourceWidth * sourceHeight * 3)
        {
            throw new ArgumentException("The output must be the source's size times a whole scale.", nameof(source));
        }

        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads) };
        var pixels = sourceWidth * sourceHeight;
        var guide = new byte[pixels];
        var chroma = new short[pixels * 2];
        Parallel.For(0, sourceHeight, options, y =>
        {
            for (var x = 0; x < sourceWidth; x++)
            {
                var sum = 0;
                for (var dy = 0; dy < scale; dy++)
                {
                    var o = ((y * scale + dy) * width + x * scale) * 3;
                    for (var dx = 0; dx < scale; dx++, o += 3)
                    {
                        sum += Luma(output[o], output[o + 1], output[o + 2]);
                    }
                }

                var p = y * sourceWidth + x;
                guide[p] = (byte)((sum + scale * scale / 2) / (scale * scale));
                int r = source[p * 3], g = source[p * 3 + 1], b = source[p * 3 + 2];
                var l = Luma(r, g, b);
                chroma[p * 2] = (short)MathF.Round(0.564f * (b - l) * ChromaQuantum);
                chroma[p * 2 + 1] = (short)MathF.Round(0.713f * (r - l) * ChromaQuantum);
            }
        });

        var smooth = new float[pixels * 2];
        var bands = (sourceHeight + SmoothBandRows - 1) / SmoothBandRows;
        Parallel.For(0, bands, options, band => SmoothBand(chroma, guide, smooth, sourceWidth, sourceHeight, band * SmoothBandRows));
        Parallel.For(0, height, options, y => ColourRow(output, width, smooth, sourceWidth, sourceHeight, scale, y));
    }

    private static void SmoothBand(short[] chroma, byte[] guide, float[] smooth, int width, int height, int top)
    {
        var tapW = SmoothTapWeights;
        var chromaW = SmoothChromaWeights;
        var side = 2 * SmoothReach + 1;
        var binShift = (int)Math.Log2(ChromaQuantum * ChromaQuantum / ChromaBins);
        Span<int> rows = stackalloc int[side];
        Span<int> cols = stackalloc int[side];
        for (var y = top; y < Math.Min(height, top + SmoothBandRows); y++)
        {
            for (var t = 0; t < side; t++)
            {
                rows[t] = Math.Clamp(y + (t - SmoothReach) * SmoothStep, 0, height - 1) * width;
            }

            for (var x = 0; x < width; x++)
            {
                var p = y * width + x;
                for (var t = 0; t < side; t++)
                {
                    cols[t] = Math.Clamp(x + (t - SmoothReach) * SmoothStep, 0, width - 1);
                }

                int cb0 = chroma[p * 2], cr0 = chroma[p * 2 + 1], y0 = guide[p];
                float sumCb = 0, sumCr = 0, sumW = 0;
                for (var ty = 0; ty < side; ty++)
                {
                    for (var tx = 0; tx < side; tx++)
                    {
                        var n = rows[ty] + cols[tx];
                        int cb = chroma[n * 2], cr = chroma[n * 2 + 1];
                        var bin = ((cb - cb0) * (cb - cb0) + (cr - cr0) * (cr - cr0)) >> binShift;
                        if (bin >= chromaW.Length)
                        {
                            continue;
                        }

                        var w = tapW[(ty * side + tx) * 256 + Math.Abs(guide[n] - y0)] * chromaW[bin];
                        sumCb += w * cb;
                        sumCr += w * cr;
                        sumW += w;
                    }
                }

                smooth[p * 2] = sumCb / sumW / (ChromaQuantum * 0.564f);
                smooth[p * 2 + 1] = sumCr / sumW / (ChromaQuantum * 0.713f);
            }
        }
    }

    /// <summary>One output row: its own luma plus the smoothed colour (blue and red minus luma) sampled up bilinearly.</summary>
    private static void ColourRow(byte[] output, int width, float[] smooth, int sourceWidth, int sourceHeight, int scale, int y)
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
            var l = Luma(output[o], output[o + 1], output[o + 2]);
            output[o] = (byte)Math.Clamp((int)MathF.Round(l + dr), 0, 255);
            output[o + 1] = (byte)Math.Clamp((int)MathF.Round(l + dg), 0, 255);
            output[o + 2] = (byte)Math.Clamp((int)MathF.Round(l + db), 0, 255);
        }
    }

    private static float Bilinear(float[] v, int a, int b, int c, int d, float fx, float fy) =>
        (v[a] * (1 - fx) + v[b] * fx) * (1 - fy) + (v[c] * (1 - fx) + v[d] * fx) * fy;

    /// <summary>Rec. 601 luma, rounded. For a grey pixel it returns that grey.</summary>
    public static int Luma(int r, int g, int b) => (299 * r + 587 * g + 114 * b + 500) / 1000;

    /// <summary>
    /// True when every pixel of interleaved RGB has R = G = B, as a one-band JPEG or PNG decodes.
    /// For such a page <see cref="TakeColourFrom"/> followed by <see cref="ToSingleBand"/> gives
    /// exactly the <see cref="Luma"/> of the model output, so the output can be stitched as luma.
    /// </summary>
    public static bool IsExactGrey(ReadOnlySpan<byte> rgb)
    {
        for (var i = 0; i + 2 < rgb.Length; i += 3)
        {
            if (rgb[i] != rgb[i + 1] || rgb[i] != rgb[i + 2])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>One band per pixel, the rounded mean of the three channels.</summary>
    public static byte[] ToSingleBand(ReadOnlySpan<byte> rgb)
    {
        var grey = new byte[rgb.Length / 3];
        for (var p = 0; p < grey.Length; p++)
        {
            grey[p] = (byte)((rgb[p * 3] + rgb[p * 3 + 1] + rgb[p * 3 + 2] + 1) / 3);
        }

        return grey;
    }
}
