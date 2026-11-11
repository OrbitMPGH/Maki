using Maki.Core.Images.Upscaling;

namespace Maki.Core.Tests;

public class UpscalePixelsTests
{
    private const int Width = 1240;
    private const int Height = 1750;

    private static byte[] GreyPage(int noise = 0)
    {
        var rgb = new byte[Width * Height * 3];
        var seed = 12345u;
        for (var p = 0; p < Width * Height; p++)
        {
            var x = p % Width;
            var y = p / Width;
            var v = (byte)(40 + (x * 3 + y * 5 + (x * y) % 23) % 200);
            seed = seed * 1664525 + 1013904223;
            var tint = noise == 0 ? 0 : (int)(seed >> 24) % (noise + 1);
            rgb[p * 3] = v;
            rgb[p * 3 + 1] = v;
            rgb[p * 3 + 2] = (byte)(v + tint);
        }

        return rgb;
    }

    private static void Paint(byte[] rgb, int left, int top, int width, int height)
    {
        for (var y = top; y < top + height; y++)
        for (var x = left; x < left + width; x++)
        {
            var o = (y * Width + x) * 3;
            (rgb[o], rgb[o + 1], rgb[o + 2]) = ((byte)210, (byte)30, (byte)40);
        }
    }

    [Fact]
    public void A_grey_page_is_grey()
    {
        Assert.True(UpscalePixels.IsGreyscale(GreyPage()));
        Assert.True(UpscalePixels.IsGreyscale([]));
    }

    [Fact]
    public void Faint_jpeg_like_tint_noise_is_still_grey()
    {
        Assert.True(UpscalePixels.IsGreyscale(GreyPage(noise: 4)));
    }

    [Fact]
    public void A_small_red_title_on_a_grey_page_is_colour()
    {
        var rgb = GreyPage(noise: 4);
        Paint(rgb, 300, 100, 124, 87);

        Assert.False(UpscalePixels.IsGreyscale(rgb));
    }

    [Fact]
    public void A_thin_red_column_is_found_whatever_the_page_width()
    {
        var rgb = GreyPage();
        Paint(rgb, 620, 0, 6, Height);

        Assert.False(UpscalePixels.IsGreyscale(rgb));
    }

    [Fact]
    public void A_coloured_page_is_colour()
    {
        var rgb = new byte[Width * Height * 3];
        for (var p = 0; p < Width * Height; p++)
        {
            (rgb[p * 3], rgb[p * 3 + 1], rgb[p * 3 + 2]) = ((byte)(p % 251), (byte)(p / Width % 256), (byte)90);
        }

        Assert.False(UpscalePixels.IsGreyscale(rgb));
    }

    [Fact]
    public void An_even_sepia_tint_is_colour()
    {
        var rgb = GreyPage();
        for (var p = 0; p < Width * Height; p++)
        {
            rgb[p * 3] = (byte)Math.Min(255, rgb[p * 3] + 10);
            rgb[p * 3 + 2] = (byte)Math.Max(0, rgb[p * 3 + 2] - 2);
        }

        Assert.False(UpscalePixels.IsGreyscale(rgb));
    }

    [Fact]
    public void A_model_tint_on_grey_is_dropped_and_its_brightness_kept()
    {
        byte[] source = [100, 100, 100, 30, 30, 30];
        byte[] output = [120, 104, 140, 10, 0, 20];

        UpscalePixels.TakeColourFrom(output, 2, 1, source, 2, 1, 1);

        // Luma of (120, 104, 140) rounds to 113 and of (10, 0, 20) to 5.
        Assert.Equal([113, 113, 113, 5, 5, 5], output);
    }

    [Fact]
    public void A_colour_source_keeps_its_colour_under_the_new_brightness()
    {
        byte[] source = [200, 40, 40];
        byte[] output = [230, 70, 70];

        UpscalePixels.TakeColourFrom(output, 1, 1, source, 1, 1, 1);

        // Source luma 88, output luma 118: every channel moves up by 30.
        Assert.Equal([230, 70, 70], output);
        Assert.Throws<ArgumentException>(() => UpscalePixels.TakeColourFrom(new byte[9], 3, 1, new byte[6], 2, 1, 1));
    }

    [Fact]
    public void On_an_exact_grey_source_the_colour_pass_and_band_average_equal_the_output_luma()
    {
        var random = new Random(601);
        var output = new byte[256 * 64 * 3];
        random.NextBytes(output);
        var source = new byte[output.Length];
        for (var p = 0; p < output.Length / 3; p++)
        {
            source[p * 3] = source[p * 3 + 1] = source[p * 3 + 2] = (byte)(p / 64);
        }

        var luma = new byte[output.Length / 3];
        for (var p = 0; p < luma.Length; p++)
        {
            luma[p] = (byte)UpscalePixels.Luma(output[p * 3], output[p * 3 + 1], output[p * 3 + 2]);
        }

        UpscalePixels.TakeColourFrom(output, 256, 64, source, 256, 64, 2);

        Assert.Equal(luma, UpscalePixels.ToSingleBand(output));
    }

    [Fact]
    public void Slightly_different_tints_in_a_flat_area_even_out_while_a_colour_edge_stays()
    {
        // Source: two 8 px JPEG-like blocks of dark purple a little apart in tint, then a saturated red
        // area. Output: the same page at 2x, as a model that changed nothing would return it.
        const int width = 32, height = 16;
        var source = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            (byte r, byte g, byte b) c = x < 8 ? ((byte)40, (byte)20, (byte)60) : x < 16 ? ((byte)44, (byte)20, (byte)56) : ((byte)200, (byte)30, (byte)30);
            var p = (y * width + x) * 3;
            (source[p], source[p + 1], source[p + 2]) = c;
        }

        var output = new byte[width * height * 12];
        for (var y = 0; y < height * 2; y++)
        for (var x = 0; x < width * 2; x++)
        {
            Array.Copy(source, ((y / 2) * width + x / 2) * 3, output, (y * width * 2 + x) * 3, 3);
        }

        UpscalePixels.TakeColourFrom(output, width * 2, height * 2, source, width, height, 2);

        int Red(int x) => output[(16 * width * 2 + x) * 3];
        var stepAfter = Math.Abs(Red(16) - Red(15));
        Assert.True(stepAfter <= 2, $"The 4-level block step is still {stepAfter}.");
        Assert.InRange(Red(48), 199, 201);
        Assert.True(Red(33) > 190, $"The red area's edge bled to {Red(33)}.");
        Assert.True(Red(30) < 60, $"The purple beside the red area picked up red: {Red(30)}.");
    }

    [Fact]
    public void Exact_grey_needs_every_pixel_to_have_equal_channels()
    {
        var rgb = GreyPage();
        Assert.True(UpscalePixels.IsExactGrey(rgb));

        rgb[^2]++;
        Assert.True(UpscalePixels.IsGreyscale(rgb));
        Assert.False(UpscalePixels.IsExactGrey(rgb));
    }
}
