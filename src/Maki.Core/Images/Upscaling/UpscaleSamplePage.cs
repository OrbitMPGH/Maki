namespace Maki.Core.Images.Upscaling;

/// <summary>
/// A synthetic greyscale manga page for timing the server models: panel borders, rows of
/// glyph-like strokes and a halftone dot tone. Generated, so nothing ships with the image.
/// </summary>
public static class UpscaleSamplePage
{
    public const int Width = 1000;
    public const int Height = 1400;

    /// <summary>The page as PNG bytes. Always the same pixels.</summary>
    public static byte[] Create()
    {
        var page = new Raster(Width, Height, new Rgba(255, 255, 255), bands: 3);
        var random = new Random(1400);

        Box(page, 40, 40, 960, 660, 4);
        Box(page, 40, 700, 480, 1360, 4);
        Box(page, 520, 700, 960, 1360, 4);

        for (var y = 60; y < 640; y++)
        for (var x = 60; x < 940; x++)
        {
            var cx = x % 8 - 4;
            var cy = y % 8 - 4;
            var radius = 1.0 + 2.5 * (x - 60) / 880.0;
            if (cx * cx + cy * cy <= radius * radius)
            {
                Set(page, x, y, 40);
            }
        }

        for (var line = 0; line < 22; line++)
        {
            var top = 730 + line * 28;
            for (var glyph = 0; glyph < 16; glyph++)
            {
                Glyph(page, random, 70 + glyph * 24, top);
                Glyph(page, random, 550 + glyph * 24, top);
            }
        }

        return page.EncodePng(1);
    }

    private static void Glyph(Raster page, Random random, int left, int top)
    {
        for (var stroke = 0; stroke < 3; stroke++)
        {
            var x0 = left + random.Next(0, 16);
            var y0 = top + random.Next(0, 18);
            var x1 = left + random.Next(0, 16);
            var y1 = top + random.Next(0, 18);
            var steps = Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0)) + 1;
            for (var s = 0; s <= steps; s++)
            {
                var x = x0 + (x1 - x0) * s / steps;
                var y = y0 + (y1 - y0) * s / steps;
                for (var dy = 0; dy < 2; dy++)
                for (var dx = 0; dx < 2; dx++)
                {
                    Set(page, x + dx, y + dy, 10);
                }
            }
        }
    }

    private static void Box(Raster page, int left, int top, int right, int bottom, int thickness)
    {
        for (var y = top; y < bottom; y++)
        for (var x = left; x < right; x++)
        {
            if (x < left + thickness || x >= right - thickness || y < top + thickness || y >= bottom - thickness)
            {
                Set(page, x, y, 0);
            }
        }
    }

    private static void Set(Raster page, int x, int y, byte value)
    {
        if (x < 0 || y < 0 || x >= page.Width || y >= page.Height)
        {
            return;
        }

        var o = (y * page.Width + x) * 3;
        page.Pixels[o] = value;
        page.Pixels[o + 1] = value;
        page.Pixels[o + 2] = value;
    }
}
