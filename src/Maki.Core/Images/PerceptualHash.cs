using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Maki.Core.Images;

/// <summary>
/// Difference hash (dHash) of an image: downscale to 9x8 grayscale, then record whether each pixel
/// is brighter than the one to its right. The result is 64 bits describing gradient direction, so
/// it survives rescaling, re-encoding and moderate contrast changes — which is exactly what
/// separates two sites' copies of the same manga page.
/// </summary>
public static class PerceptualHash
{
    public static async Task<ulong> OfFileAsync(string path, CancellationToken ct = default)
    {
        using var image = await Image.LoadAsync<L8>(path, ct);
        return Of(image);
    }

    public static ulong Of(Image<L8> image)
    {
        // Box resampling, not the default bicubic: no ringing or sharpening, so two copies of the
        // same page at different resolutions land on the same gradients.
        image.Mutate(x => x.Resize(9, 8, KnownResamplers.Box));

        ulong hash = 0;
        var bit = 0;
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < 8; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < 8; x++)
                {
                    if (row[x].PackedValue > row[x + 1].PackedValue)
                    {
                        hash |= 1UL << bit;
                    }

                    bit++;
                }
            }
        });

        return hash;
    }

    /// <summary>Differing bits between two hashes, 0 (identical) to 64 (nothing in common).</summary>
    public static int Distance(ulong a, ulong b) => System.Numerics.BitOperations.PopCount(a ^ b);
}
