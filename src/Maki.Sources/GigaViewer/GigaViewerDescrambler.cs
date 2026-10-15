using Maki.Core.Images;

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
        var source = Raster.Decode(bytes, bands: 3);
        var blockWidth = source.Width / 32 * 8;
        var blockHeight = source.Height / 32 * 8;
        if (blockWidth == 0 || blockHeight == 0)
        {
            return bytes;
        }

        TransposeInPlace(source, blockWidth, blockHeight);
        return source.EncodeJpeg(90);
    }

    /// <summary>The pixel transform alone, for unit testing without a lossy JPEG round trip.</summary>
    public static Raster Descramble(Raster source)
    {
        var result = source.Clone();
        var blockWidth = source.Width / 32 * 8;
        var blockHeight = source.Height / 32 * 8;
        if (blockWidth > 0 && blockHeight > 0)
        {
            TransposeInPlace(result, blockWidth, blockHeight);
        }

        return result;
    }

    /// <summary>
    /// Swaps each block (row, col) with (col, row) through one block-sized buffer. The diagonal
    /// stays put. Drawing into a clone held a second full page of pixels for every descramble.
    /// </summary>
    private static void TransposeInPlace(Raster image, int blockWidth, int blockHeight)
    {
        var rowBytes = blockWidth * image.Bands;
        var block = new byte[rowBytes * blockHeight];
        for (var row = 0; row < 4; row++)
        {
            for (var col = row + 1; col < 4; col++)
            {
                for (var y = 0; y < blockHeight; y++)
                {
                    image.Row(row * blockHeight + y).Slice(col * rowBytes, rowBytes)
                        .CopyTo(block.AsSpan(y * rowBytes, rowBytes));
                }

                for (var y = 0; y < blockHeight; y++)
                {
                    image.Row(col * blockHeight + y).Slice(row * rowBytes, rowBytes)
                        .CopyTo(image.Row(row * blockHeight + y).Slice(col * rowBytes, rowBytes));
                }

                for (var y = 0; y < blockHeight; y++)
                {
                    block.AsSpan(y * rowBytes, rowBytes)
                        .CopyTo(image.Row(col * blockHeight + y).Slice(row * rowBytes, rowBytes));
                }
            }
        }
    }
}
