using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Maki.Sources.GigaViewer;

/// <summary>
/// Reverses GigaViewer's ("choJuGiga": "baku") page scramble: the image is cut into a 4x4 grid
/// of blocks (each block's size floored to a multiple of 8px, which leaves a strip on the
/// bottom/right edges outside the grid untouched) and the blocks are transposed, (row, col)
/// swapping with (col, row). The transpose is its own inverse, which is what the descrambler
/// unit test round-trips through.
/// </summary>
public static class GigaViewerDescrambler
{
    /// <summary>Decodes, descrambles and re-encodes as JPEG. Untouched bytes back when the image
    /// is too small for a 4x4 grid (bw or bh floors to 0) rather than a pointless re-encode.</summary>
    public static byte[] Descramble(byte[] bytes)
    {
        using var source = Image.Load<Rgba32>(bytes);
        var blockWidth = source.Width / 32 * 8;
        var blockHeight = source.Height / 32 * 8;
        if (blockWidth == 0 || blockHeight == 0)
        {
            return bytes;
        }

        using var result = Transpose(source, blockWidth, blockHeight);
        using var stream = new MemoryStream();
        result.SaveAsJpeg(stream, new JpegEncoder { Quality = 90 });
        return stream.ToArray();
    }

    /// <summary>The pixel transform alone, for unit testing without a lossy JPEG round trip.</summary>
    public static Image<Rgba32> Descramble(Image<Rgba32> source)
    {
        var blockWidth = source.Width / 32 * 8;
        var blockHeight = source.Height / 32 * 8;
        return Transpose(source, blockWidth, blockHeight);
    }

    private static Image<Rgba32> Transpose(Image<Rgba32> source, int blockWidth, int blockHeight)
    {
        var result = source.Clone();
        if (blockWidth == 0 || blockHeight == 0)
        {
            return result;
        }

        result.Mutate(ctx =>
        {
            for (var i = 0; i < 16; i++)
            {
                var srcX = i % 4 * blockWidth;
                var srcY = i / 4 * blockHeight;
                var dstX = i / 4 * blockWidth;
                var dstY = i % 4 * blockHeight;

                // Crop from the original (source), never from the in-progress result, so
                // reading block N doesn't pick up a block already written this pass.
                ctx.DrawImage(source, new Point(dstX, dstY), new Rectangle(srcX, srcY, blockWidth, blockHeight), 1f);
            }
        });

        return result;
    }
}
