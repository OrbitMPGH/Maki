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
}
