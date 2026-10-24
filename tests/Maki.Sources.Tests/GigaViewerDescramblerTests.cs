using Maki.Sources.GigaViewer;
using Maki.Core.Images;

namespace Maki.Sources.Tests;

public class GigaViewerDescramblerTests
{
    private const int Width = 128;
    private const int Height = 96;
    private const int BlockWidth = 32; // 128/32*8
    private const int BlockHeight = 24; // 96/32*8

    /// <summary>
    /// A non-square image (so a width/height mixup in the block-size math would be visible)
    /// with 16 distinctly-coloured blocks, built twice from scratch: once in the readable
    /// ("original") arrangement, once independently in the scrambled arrangement the plan
    /// describes (block i's colour at column i/4, row i%4). Descrambling the second must
    /// produce exactly the first, block for block, not merely round-trip through itself.
    /// </summary>
    [Fact]
    public void Descramble_MapsTheScrambledGridBackToTheOriginalLayout()
    {
        var colors = BuildDistinctColors();
        var original = BuildGrid(colors, blockAt: i => (i % 4, i / 4));
        var scrambled = BuildGrid(colors, blockAt: i => (i / 4, i % 4));

        var result = GigaViewerDescrambler.Descramble(scrambled);

        AssertPixelsEqual(original, result);

        // Explicit off-diagonal checks: block 1 (original col=1,row=0) and block 4 (original
        // col=0,row=1) are not on the diagonal, so a transpose that only handled the diagonal
        // blocks correctly (or swapped width/height) would still fail here.
        AssertBlockColor(result, colors[1], col: 1, row: 0);
        AssertBlockColor(result, colors[4], col: 0, row: 1);

        // And the scrambled input really did differ from the original, or the assertions above
        // would pass even for a no-op descramble.
        Assert.False(PixelsEqual(original, scrambled));
    }

    [Fact]
    public void Descramble_ProducesDecodableJpegBytes()
    {
        var colors = BuildDistinctColors();
        var scrambled = BuildGrid(colors, blockAt: i => (i / 4, i % 4));

        var result = GigaViewerDescrambler.Descramble(scrambled.EncodePng());

        var decoded = ImageCodec.Identify(result);
        Assert.Equal(Width, decoded.Width);
        Assert.Equal(Height, decoded.Height);
        Assert.Equal(ImageFormat.Jpeg, decoded.Format);
    }

    /// <summary>
    /// 100x64 floors to a 24px block (100/32*8), covering only 96 of the 100 columns; the
    /// rightmost 4-column margin sits outside the 4x4 grid and must survive untouched.
    /// </summary>
    [Fact]
    public void Descramble_LeavesTheMarginOutsideTheBlockGridUntouched()
    {
        var source = new Raster(100, 64);
        var blue = new Rgba(100, 149, 237);
        var crimson = new Rgba(220, 20, 60);
        for (var y = 0; y < 64; y++)
        {
            for (var x = 0; x < 100; x++)
            {
                source[x, y] = x >= 96 ? crimson : blue;
            }
        }

        var result = GigaViewerDescrambler.Descramble(source);

        for (var y = 0; y < 64; y++)
        {
            for (var x = 96; x < 100; x++)
            {
                Assert.Equal(source[x, y], result[x, y]);
            }
        }
    }

    [Fact]
    public void Descramble_ReturnsBytesUntouchedWhenTooSmallForAGrid()
    {
        var tiny = new Raster(16, 16);
        var yellow = new Rgba(255, 255, 0);
        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                tiny[x, y] = yellow;
            }
        }

        var bytes = tiny.EncodePng();

        var result = GigaViewerDescrambler.Descramble(bytes);

        Assert.Equal(bytes, result);
    }

    private static Rgba[] BuildDistinctColors() =>
        Enumerable.Range(0, 16)
            .Select(i => new Rgba((byte)(i * 16), (byte)(255 - i * 16), (byte)(i * 8 + 4), 255))
            .ToArray();

    private static Raster BuildGrid(Rgba[] colors, Func<int, (int Col, int Row)> blockAt)
    {
        var image = new Raster(Width, Height);
        for (var i = 0; i < 16; i++)
        {
            var (col, row) = blockAt(i);
            var blockX = col * BlockWidth;
            var blockY = row * BlockHeight;
            for (var y = 0; y < BlockHeight; y++)
            {
                for (var x = 0; x < BlockWidth; x++)
                {
                    image[blockX + x, blockY + y] = colors[i];
                }
            }
        }

        return image;
    }

    private static void AssertBlockColor(Raster image, Rgba expected, int col, int row)
    {
        // Sample the block's centre, not its corner, so an off-by-one in the block math
        // still fails the assertion instead of accidentally landing on the right pixel.
        var x = col * BlockWidth + BlockWidth / 2;
        var y = row * BlockHeight + BlockHeight / 2;
        Assert.Equal(expected, image[x, y]);
    }

    private static void AssertPixelsEqual(Raster a, Raster b)
    {
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                Assert.Equal(a[x, y], b[x, y]);
            }
        }
    }

    private static bool PixelsEqual(Raster a, Raster b)
    {
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                if (a[x, y] != b[x, y])
                {
                    return false;
                }
            }
        }

        return true;
    }
}
