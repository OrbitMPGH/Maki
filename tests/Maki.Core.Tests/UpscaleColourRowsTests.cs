using Maki.Core.Images.Upscaling;

namespace Maki.Core.Tests;

/// <summary>
/// <see cref="UpscaleColourRows"/> colours an output that arrives a few rows at a time. It has to give the bytes
/// <see cref="UpscalePixels.TakeColourFrom(byte[], int, int, byte[], int, int, int)"/> gives the whole page.
/// </summary>
public class UpscaleColourRowsTests
{
    [Theory]
    [InlineData(37, 5, 2, 1, 7)]
    [InlineData(64, 33, 2, 2, 64)]
    [InlineData(50, 200, 2, 3, 100)]
    [InlineData(31, 97, 1, 2, 13)]
    [InlineData(17, 70, 4, 7, 50)]
    [InlineData(80, 1000, 2, 4, 2056)]
    [InlineData(23, 400, 2, 18, 333)]
    public void Rows_coloured_as_they_arrive_match_the_whole_page_pass(
        int sourceWidth, int sourceHeight, int scale, int threads, int step)
    {
        var (source, output) = Page(sourceWidth, sourceHeight, scale, seed: sourceWidth * 17 + sourceHeight);
        var width = sourceWidth * scale;
        var height = sourceHeight * scale;
        var expected = (byte[])output.Clone();
        UpscalePixels.TakeColourFrom(expected, width, height, source, sourceWidth, sourceHeight, threads);

        var colour = new UpscaleColourRows(source, sourceWidth, sourceHeight, scale);
        UpscaleColourRows.OutputRow row = y => output.AsSpan(y * width * 3, width * 3);
        // Ask for strips as an encoder would, with the stitched rows only as far as each strip needs.
        for (var coloured = 0; coloured < height;)
        {
            var end = Math.Min(height, coloured + step);
            colour.AddGuide(colour.RowsNeededFor(end), row);
            colour.Colour(coloured, end, row, threads);
            coloured = end;
        }

        Assert.Equal(expected, output);
    }

    [Fact]
    public void Colouring_past_the_guide_is_refused()
    {
        var (source, output) = Page(20, 100, 2, seed: 3);
        var colour = new UpscaleColourRows(source, 20, 100, 2);
        UpscaleColourRows.OutputRow row = y => output.AsSpan(y * 120, 120);

        colour.AddGuide(40, row);

        Assert.Throws<InvalidOperationException>(() => colour.Colour(0, 40, row, 1));
    }

    private static (byte[] Source, byte[] Output) Page(int width, int height, int scale, int seed)
    {
        var random = new Random(seed);
        var source = new byte[width * height * 3];
        for (var p = 0; p < width * height; p++)
        {
            var (x, y) = (p % width, p / width);
            var block = (x / 8 + y / 8) % 4;
            source[p * 3] = (byte)(block == 0 ? 200 : random.Next(256));
            source[p * 3 + 1] = (byte)(block == 1 ? 30 + random.Next(3) : x * 3 % 256);
            source[p * 3 + 2] = (byte)(block == 2 ? 128 : (x + y) % 256);
        }

        var output = new byte[width * scale * height * scale * 3];
        for (var p = 0; p < output.Length / 3; p++)
        {
            var (x, y) = (p % (width * scale) / scale, p / (width * scale) / scale);
            var luma = (byte)Math.Clamp(source[(y * width + x) * 3 + 1] + random.Next(-6, 7), 0, 255);
            output[p * 3] = (byte)Math.Clamp(luma + random.Next(-3, 4), 0, 255);
            output[p * 3 + 1] = luma;
            output[p * 3 + 2] = (byte)Math.Clamp(luma + random.Next(-3, 4), 0, 255);
        }

        return (source, output);
    }
}
