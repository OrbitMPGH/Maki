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
