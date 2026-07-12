using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Mangarr.Core.Download;

/// <summary>
/// Reverses MangaFire's tile scrambling. The site splits an image into pieces of at
/// most 200×200 px (at least a 5×5 grid) and shuffles all but the last row/column by
/// a per-image offset from the ajax response. Ported from the Tachiyomi MangaFire
/// extension's ImageInterceptor (removed upstream when the site changed APIs).
/// </summary>
public static class MangaFireDescrambler
{
    private const int PieceSize = 200;
    private const int MinSplitCount = 5;

    /// <summary>Descrambles the image file in place.</summary>
    public static async Task DescrambleFileAsync(string filePath, int offset, CancellationToken ct = default)
    {
        using var source = await Image.LoadAsync(filePath, ct);
        using var result = Descramble(source, offset);

        var extension = Path.GetExtension(filePath);
        if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            await result.SaveAsJpegAsync(filePath, new JpegEncoder { Quality = 90 }, ct);
        }
        else
        {
            await result.SaveAsync(filePath, ct); // encoder chosen by file extension
        }
    }

    public static Image Descramble(Image source, int offset)
    {
        var width = source.Width;
        var height = source.Height;
        var pieceWidth = Math.Min(PieceSize, CeilDiv(width, MinSplitCount));
        var pieceHeight = Math.Min(PieceSize, CeilDiv(height, MinSplitCount));
        var xMax = CeilDiv(width, pieceWidth) - 1;
        var yMax = CeilDiv(height, pieceHeight) - 1;

        var result = new Image<Rgba32>(width, height);
        result.Mutate(ctx =>
        {
            for (var y = 0; y <= yMax; y++)
            {
                for (var x = 0; x <= xMax; x++)
                {
                    var xDst = pieceWidth * x;
                    var yDst = pieceHeight * y;
                    var w = Math.Min(pieceWidth, width - xDst);
                    var h = Math.Min(pieceHeight, height - yDst);

                    // The last row/column stay in place; the rest are shuffled by the offset.
                    var xSrc = pieceWidth * (x == xMax ? x : (xMax - x + offset) % xMax);
                    var ySrc = pieceHeight * (y == yMax ? y : (yMax - y + offset) % yMax);

                    ctx.DrawImage(source, new Point(xDst, yDst), new Rectangle(xSrc, ySrc, w, h), 1f);
                }
            }
        });

        return result;
    }

    private static int CeilDiv(int value, int divisor) => (value + divisor - 1) / divisor;
}
