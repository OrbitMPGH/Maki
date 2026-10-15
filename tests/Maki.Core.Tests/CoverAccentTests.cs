using Maki.Core.Images;

namespace Maki.Core.Tests;

public class CoverAccentTests
{
    private static (int R, int G, int B) Parse(string hex) =>
        (Convert.ToInt32(hex[1..3], 16), Convert.ToInt32(hex[3..5], 16), Convert.ToInt32(hex[5..7], 16));

    [Fact]
    public void A_red_cover_lends_a_red()
    {
        var raster = new Raster(60, 84, new Rgba(190, 28, 46), bands: 3);

        var hex = CoverAccent.FromImage(raster.EncodeJpeg(90));

        Assert.NotNull(hex);
        var (r, g, b) = Parse(hex);
        Assert.True(r > g + 60 && r > b + 60, hex);
    }

    [Fact]
    public void A_greyscale_cover_lends_nothing()
    {
        var raster = new Raster(60, 84, bands: 3);
        for (var y = 0; y < raster.Height; y++)
        for (var x = 0; x < raster.Width; x++)
        {
            var v = (byte)(40 + (x + y) % 160);
            raster[x, y] = new Rgba(v, v, v);
        }

        Assert.Null(CoverAccent.FromImage(raster.EncodeJpeg(90)));
    }

    [Fact]
    public void The_dominant_hue_wins_over_a_smaller_patch()
    {
        var raster = new Raster(60, 84, new Rgba(30, 70, 200), bands: 3);
        for (var y = 0; y < 20; y++)
        for (var x = 0; x < 20; x++)
        {
            raster[x, y] = new Rgba(230, 200, 20);
        }

        var hex = CoverAccent.FromImage(raster.EncodeJpeg(90));

        Assert.NotNull(hex);
        var (r, g, b) = Parse(hex);
        Assert.True(b > r && b > g, hex);
    }

    [Fact]
    public void Every_accent_sits_at_one_lightness()
    {
        var hues = new[] { new Rgba(220, 40, 40), new Rgba(40, 200, 60), new Rgba(250, 220, 30), new Rgba(40, 60, 230) };
        var lightness = hues
            .Select(fill => CoverAccent.FromImage(new Raster(40, 56, fill, bands: 3).EncodeJpeg(90)))
            .Select(hex =>
            {
                Assert.NotNull(hex);
                var (r, g, b) = Parse(hex);
                return OkLab.FromSrgb((byte)r, (byte)g, (byte)b).L;
            })
            .ToList();

        Assert.All(lightness, l => Assert.InRange(l, 0.59, 0.65));
    }
}
