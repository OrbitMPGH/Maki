using NetVips;

namespace Maki.Core.Images;

/// <summary>
/// Difference hash (dHash) of an image: downscale to 9x8 grayscale, then record whether each pixel
/// is brighter than the one to its right. The result is 64 bits describing gradient direction, so
/// it survives rescaling, re-encoding and moderate contrast changes — which is exactly what
/// separates two sites' copies of the same manga page.
/// </summary>
public static class PerceptualHash
{
    private const int Cell = 16;

    public static ulong OfFile(string path)
    {
        // A 144x128 thumbnail first, so a JPEG shrinks while decoding instead of the whole page
        // existing as pixels; the aspect ratio does not matter, the hash squashes it anyway.
        using var thumb = ImageCodec.ThumbnailFile(path, 9 * Cell, 8 * Cell);
        // Through 8-bit sRGB first: straight to greyscale, a 16-bit PNG would stay 16-bit.
        using var srgb = thumb.Colourspace(Enums.Interpretation.Srgb);
        using var grey = srgb.Colourspace(Enums.Interpretation.Bw);
        using var flat = grey.HasAlpha() ? grey.Flatten(background: [255]) : grey.Copy();
        return Of(flat.WriteToMemory<byte>(), flat.Width, flat.Height);
    }

    /// <summary>Hash of one-byte-per-pixel greyscale, averaged into a 9x8 grid of boxes.</summary>
    public static ulong Of(byte[] grey, int width, int height)
    {
        // Box averaging, not a smoothing filter: no ringing or sharpening, so two copies of the
        // same page at different resolutions land on the same gradients.
        var cells = new long[9 * 8];
        var counts = new long[9 * 8];
        for (var y = 0; y < height; y++)
        {
            var cy = y * 8 / height;
            for (var x = 0; x < width; x++)
            {
                var cell = cy * 9 + x * 9 / width;
                cells[cell] += grey[y * width + x];
                counts[cell]++;
            }
        }

        ulong hash = 0;
        var bit = 0;
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                var left = cells[y * 9 + x] * counts[y * 9 + x + 1];
                var right = cells[y * 9 + x + 1] * counts[y * 9 + x];
                if (left > right)
                {
                    hash |= 1UL << bit;
                }

                bit++;
            }
        }

        return hash;
    }

    /// <summary>Differing bits between two hashes, 0 (identical) to 64 (nothing in common).</summary>
    public static int Distance(ulong a, ulong b) => System.Numerics.BitOperations.PopCount(a ^ b);
}
