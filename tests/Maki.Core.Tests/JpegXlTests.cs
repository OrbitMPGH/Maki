using Maki.Core.Images;
using Maki.Core.Images.Upscaling;

namespace Maki.Core.Tests;

/// <summary>
/// Skipped where libjxl is not beside the tests, unless <c>MAKI_REQUIRE_LIBJXL=1</c> (CI builds it
/// with <c>distribution/libjxl/build.sh</c> and sets that, so a missing library fails instead).
/// </summary>
public sealed class JpegXlFactAttribute : FactAttribute
{
    public JpegXlFactAttribute()
    {
        if (!JpegXl.IsAvailable && Environment.GetEnvironmentVariable("MAKI_REQUIRE_LIBJXL") != "1")
        {
            Skip = "libjxl is not installed beside the tests";
        }
    }
}

public class JpegXlTests
{
    private static byte[] Gradient(int width, int height)
    {
        var rgb = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 3;
                rgb[i] = (byte)(x * 255 / (width - 1));
                rgb[i + 1] = (byte)(y * 255 / (height - 1));
                rgb[i + 2] = (byte)(40 + (x + y) % 32);
            }
        }

        return rgb;
    }

    private static double MeanError(byte[] a, byte[] b)
    {
        long sum = 0;
        for (var i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return (double)sum / a.Length;
    }

    [JpegXlFact]
    public void A_colour_page_comes_back_the_same_size_and_close_to_the_source()
    {
        var rgb = Gradient(300, 200);
        var jxl = JpegXl.Encode(rgb, 300, 200, 3, 1.5f, 3);
        Assert.Equal(0xFF, jxl[0]);
        Assert.Equal(0x0A, jxl[1]);

        var (pixels, width, height, bands) = JpegXl.Decode(jxl, 1_000_000);
        Assert.Equal((300, 200, 3), (width, height, bands));
        Assert.True(MeanError(rgb, pixels) < 4, $"mean error {MeanError(rgb, pixels):F2}");
    }

    [JpegXlFact]
    public void A_grey_page_stays_one_band()
    {
        var grey = Enumerable.Range(0, 120 * 80).Select(i => (byte)(i % 120 * 2)).ToArray();
        var (_, _, _, bands) = JpegXl.Decode(JpegXl.Encode(grey, 120, 80, 1, 1.0f, 3), 1_000_000);
        Assert.Equal(1, bands);
    }

    [JpegXlFact]
    public void Decode_refuses_an_image_over_the_limit()
    {
        var jxl = JpegXl.Encode(Gradient(100, 100), 100, 100, 3, 1.5f, 3);
        Assert.Throws<JpegXlException>(() => JpegXl.Decode(jxl, 9_999));
    }

    [JpegXlFact]
    public void Garbage_is_a_jpeg_xl_exception_not_a_crash()
    {
        Assert.Throws<JpegXlException>(() => JpegXl.Decode([0xFF, 0x0A, 1, 2, 3, 4, 5, 6], 1_000_000));
    }

    [JpegXlFact]
    public void An_upscaled_colour_page_is_jpeg_xl_only_when_asked_and_within_the_pixel_cap()
    {
        var rgb = Gradient(64, 48);
        Assert.Equal("jxl", ImageCodec.EncodeUpscaled(rgb, 64, 48, grey: false, jpegXl: true).Extension);
        Assert.Equal("webp", ImageCodec.EncodeUpscaled(rgb, 64, 48, grey: false, jpegXl: false).Extension);
        Assert.Equal(("webp", 1), ImageCodec.EncodeUpscaled(rgb, 64, 48, grey: true, jpegXl: true) is var g ? (g.Extension, g.Bands) : default);

        var (width, height) = (4000, (int)(ImageCodec.MaxJpegXlPixels / 4000) + 1);
        var over = ImageCodec.EncodeUpscaled(new byte[width * height * 3], width, height, grey: false, jpegXl: true);
        Assert.Equal("webp", over.Extension);
    }

    [JpegXlFact]
    public void A_cached_jpeg_xl_page_converts_to_the_webp_the_pipeline_would_have_written()
    {
        var rgb = Gradient(64, 48);
        var jxl = ImageCodec.EncodeUpscaled(rgb, 64, 48, grey: false, jpegXl: true);
        var webp = ImageCodec.TranscodeUpscaledJpegXl(jxl.Bytes);
        Assert.Equal(("webp", 64, 48, 3), (webp.Extension, webp.Width, webp.Height, webp.Bands));
        Assert.Equal(ImageFormat.Webp, ImageCodec.DetectFormat(webp.Bytes));
    }

    [Fact]
    public void Without_libjxl_a_colour_page_stays_webp_rather_than_failing()
    {
        if (JpegXl.IsAvailable) return;
        Assert.Equal("webp", ImageCodec.EncodeUpscaled(Gradient(64, 48), 64, 48, grey: false, jpegXl: true).Extension);
        Assert.Null(JpegXl.Version);
    }
}
