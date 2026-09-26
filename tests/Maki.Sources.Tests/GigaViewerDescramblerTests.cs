using Maki.Sources.GigaViewer;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

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
        using var original = BuildGrid(colors, blockAt: i => (i % 4, i / 4));
        using var scrambled = BuildGrid(colors, blockAt: i => (i / 4, i % 4));

        using var result = GigaViewerDescrambler.Descramble(scrambled);

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
        using var scrambled = BuildGrid(colors, blockAt: i => (i / 4, i % 4));
        using var stream = new MemoryStream();
        scrambled.SaveAsPng(stream);

        var result = GigaViewerDescrambler.Descramble(stream.ToArray());

        using var decoded = Image.Load(result);
        Assert.Equal(Width, decoded.Width);
        Assert.Equal(Height, decoded.Height);
        Assert.Equal("JPEG", decoded.Metadata.DecodedImageFormat?.Name);
    }

    /// <summary>
    /// 100x64 floors to a 24px block (100/32*8), covering only 96 of the 100 columns; the
    /// rightmost 4-column margin sits outside the 4x4 grid and must survive untouched.
    /// </summary>
    [Fact]
    public void Descramble_LeavesTheMarginOutsideTheBlockGridUntouched()
    {
        using var source = new Image<Rgba32>(100, 64);
        var blue = new Rgba32(100, 149, 237, 255);
        var crimson = new Rgba32(220, 20, 60, 255);
        for (var y = 0; y < 64; y++)
        {
            for (var x = 0; x < 100; x++)
            {
                source[x, y] = x >= 96 ? crimson : blue;
            }
        }

        using var result = GigaViewerDescrambler.Descramble(source);

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
        using var tiny = new Image<Rgba32>(16, 16);
        var yellow = new Rgba32(255, 255, 0, 255);
        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                tiny[x, y] = yellow;
            }
        }

        using var stream = new MemoryStream();
        tiny.SaveAsPng(stream);
        var bytes = stream.ToArray();

        var result = GigaViewerDescrambler.Descramble(bytes);

        Assert.Equal(bytes, result);
    }

    private static Rgba32[] BuildDistinctColors() =>
        Enumerable.Range(0, 16)
            .Select(i => new Rgba32((byte)(i * 16), (byte)(255 - i * 16), (byte)(i * 8 + 4), 255))
            .ToArray();

    private static Image<Rgba32> BuildGrid(Rgba32[] colors, Func<int, (int Col, int Row)> blockAt)
    {
        var image = new Image<Rgba32>(Width, Height);
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

    private static void AssertBlockColor(Image<Rgba32> image, Rgba32 expected, int col, int row)
    {
        // Sample the block's centre, not its corner, so an off-by-one in the block math
        // still fails the assertion instead of accidentally landing on the right pixel.
        var x = col * BlockWidth + BlockWidth / 2;
        var y = row * BlockHeight + BlockHeight / 2;
        Assert.Equal(expected, image[x, y]);
    }

    private static void AssertPixelsEqual(Image<Rgba32> a, Image<Rgba32> b)
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

    private static bool PixelsEqual(Image<Rgba32> a, Image<Rgba32> b)
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
