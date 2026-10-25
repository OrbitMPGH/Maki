namespace Maki.Core.Images.Upscaling;

public static class UpscalePixels
{
    private const int MaxSamples = 1 << 16;

    /// <summary>
    /// True when interleaved 8-bit RGB is grey: the mean absolute difference between channels over
    /// up to 65536 evenly spaced pixels is under 1.0.
    /// </summary>
    public static bool IsGreyscale(ReadOnlySpan<byte> rgb)
    {
        var pixels = rgb.Length / 3;
        if (pixels == 0)
        {
            return true;
        }

        var step = Math.Max(1, pixels / MaxSamples);
        long sum = 0;
        long count = 0;
        for (var p = 0; p < pixels; p += step)
        {
            int r = rgb[p * 3], g = rgb[p * 3 + 1], b = rgb[p * 3 + 2];
            sum += Math.Abs(r - g) + Math.Abs(g - b) + Math.Abs(b - r);
            count++;
        }

        return sum < count * 3;
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
