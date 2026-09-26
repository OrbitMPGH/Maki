using Maki.Sources.GigaViewer;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Maki.Sources.Tests;

public class GigaViewerDescramblerTests
{
    /// <summary>
    /// The 4x4 block transpose is its own inverse, so scrambling and descrambling are the same
    /// operation: running a known 16-colour grid through it twice must return the original grid.
    /// A 64x64 image with 16px blocks divides evenly into the 4x4 grid (64/32*8 = 16), so there
    /// is no margin to complicate the comparison.
    /// </summary>
    [Fact]
    public void Descramble_RoundTripsA16BlockGrid()
    {
        using var original = BuildBlockGrid(64, 64, blockSize: 16);

        using var scrambled = GigaViewerDescrambler.Descramble(original);
        using var restored = GigaViewerDescrambler.Descramble(scrambled);

        AssertPixelsEqual(original, restored);
        // And the scramble pass actually moved something, or the test would pass vacuously.
        Assert.False(PixelsEqual(original, scrambled));
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

    private static Image<Rgba32> BuildBlockGrid(int width, int height, int blockSize)
    {
        var image = new Image<Rgba32>(width, height);
        for (var i = 0; i < 16; i++)
        {
            var blockX = i % 4 * blockSize;
            var blockY = i / 4 * blockSize;
            var color = new Rgba32((byte)(i * 16), (byte)(255 - i * 16), (byte)(i * 8 + 4), 255);
            for (var y = 0; y < blockSize; y++)
            {
                for (var x = 0; x < blockSize; x++)
                {
                    image[blockX + x, blockY + y] = color;
                }
            }
        }

        return image;
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
