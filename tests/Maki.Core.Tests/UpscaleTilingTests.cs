using Maki.Core.Images;
using Maki.Core.Images.Upscaling;

namespace Maki.Core.Tests;

public class UpscaleTilingTests
{
    [Theory]
    [InlineData(0, 5, 0)]
    [InlineData(4, 5, 4)]
    [InlineData(5, 5, 3)]
    [InlineData(-1, 5, 1)]
    [InlineData(-16, 5, 0)]
    [InlineData(20, 5, 4)]
    [InlineData(7, 1, 0)]
    [InlineData(-3, 2, 1)]
    public void Reflect_matches_numpy_reflect_padding(int index, int length, int expected)
    {
        Assert.Equal(expected, UpscaleTiling.Reflect(index, length));
    }

    [Fact]
    public void Tiles_cover_the_page_exactly_once()
    {
        var tiles = UpscaleTiling.Plan(900, 1300);
        var covered = new int[900 * 1300];
        foreach (var tile in tiles)
        for (var y = tile.Y; y < tile.Y + tile.Height; y++)
        for (var x = tile.X; x < tile.X + tile.Width; x++)
            covered[y * 900 + x]++;

        Assert.All(covered, c => Assert.Equal(1, c));
        Assert.Equal(10 * 14, tiles.Count);
        Assert.Equal(new UpscaleTile(810, 1208, 90, 92, 12, 114, 117), tiles[^1]);
    }

    [Theory]
    [InlineData(1672, 2400, 4)]
    [InlineData(1672, 2400, 1)]
    [InlineData(720, 16000, 4)]
    [InlineData(385, 769, 1)]
    public void A_page_splits_into_as_many_tiles_as_fixed_tiling_with_equal_spans_and_one_shape(int width, int height, int multiple)
    {
        var tiles = UpscaleTiling.Plan(width, height, multiple);

        var columns = tiles.Select(t => (t.X, t.Width)).Distinct().ToList();
        var rows = tiles.Select(t => (t.Y, t.Height)).Distinct().ToList();
        Assert.Equal((width + 95) / 96, columns.Count);
        Assert.Equal((height + 95) / 96, rows.Count);
        Assert.Equal(width, columns.Sum(c => c.Width));
        Assert.Equal(height, rows.Sum(r => r.Height));
        Assert.True(columns.Max(c => c.Width) - columns.Min(c => c.Width) <= 1);
        Assert.True(rows.Max(r => r.Height) - rows.Min(r => r.Height) <= 1);
        var shape = Assert.Single(tiles.Select(t => (t.ContextWidth, t.ContextHeight)).Distinct());
        Assert.Equal((0, 0), (shape.ContextWidth % multiple, shape.ContextHeight % multiple));
        Assert.All(tiles, t => Assert.True(t.ContextWidth >= t.Width + 24 && t.ContextHeight >= t.Height + 24));
    }

    [Theory]
    [InlineData(96, 120)]
    [InlineData(85, 112)]
    [InlineData(1, 28)]
    public void The_context_tile_rounds_up_to_a_multiple_of_four(int interior, int context)
    {
        var tile = UpscaleTiling.Plan(interior, interior, multiple: 4).Single();

        Assert.Equal(context, tile.ContextWidth);
        Assert.Equal(context, tile.ContextHeight);
        Assert.Equal(0, tile.ContextWidth % 4);
    }

    [Fact]
    public void Context_past_the_page_edge_is_reflected_from_the_page()
    {
        var tile = UpscaleTiling.Plan(90, 90, multiple: 4).Single();

        Assert.Equal(12, tile.SourceX(0, 90));
        Assert.Equal(0, tile.SourceX(12, 90));
        Assert.Equal(89, tile.SourceX(101, 90));
        Assert.Equal(88, tile.SourceX(102, 90));
        Assert.Equal(116, tile.ContextWidth);
    }

    [Fact]
    public void Grey_pixels_pass_the_greyscale_test_and_colour_does_not()
    {
        var grey = new byte[300];
        for (var i = 0; i < 100; i++)
        {
            grey[i * 3] = grey[i * 3 + 1] = grey[i * 3 + 2] = (byte)(i * 2);
        }

        Assert.True(UpscalePixels.IsGreyscale(grey));

        // JPEG-sized drift on one channel is still grey.
        for (var i = 0; i < 100; i++) grey[i * 3] += 4;
        Assert.True(UpscalePixels.IsGreyscale(grey));

        // An even tint of 8 is not.
        for (var i = 0; i < 100; i++) grey[i * 3] += 4;
        Assert.False(UpscalePixels.IsGreyscale(grey));

        Assert.Equal([1, 2], UpscalePixels.ToSingleBand([0, 1, 2, 2, 2, 3]));
    }

    [Fact]
    public void A_greyscale_png_decodes_for_upscaling_as_three_equal_bands()
    {
        using var grey = (NetVips.Image.Black(20, 10) + 77).Cast(NetVips.Enums.BandFormat.Uchar);
        var png = grey.PngsaveBuffer();

        var (raster, skip) = ImageCodec.DecodeForUpscale(png);

        Assert.Null(skip);
        Assert.NotNull(raster);
        Assert.Equal(3, raster.Bands);
        Assert.Equal((20, 10), (raster.Width, raster.Height));
        Assert.Equal(new Rgba(77, 77, 77), raster[5, 5]);
    }

    [Fact]
    public void A_page_with_a_wide_gamut_profile_decodes_for_upscaling_as_srgb()
    {
        using var srgb = (NetVips.Image.Black(8, 8, bands: 3) + new double[] { 200, 30, 60 })
            .Cast(NetVips.Enums.BandFormat.Uchar).Copy(interpretation: NetVips.Enums.Interpretation.Srgb);
        using var p3 = srgb.IccTransform("p3", inputProfile: "srgb", depth: 8);
        var tagged = p3.PngsaveBuffer();
        using var stored = NetVips.Image.NewFromBuffer(tagged);
        Assert.True(stored.Contains("icc-profile-data"));
        Assert.NotEqual(200, (int)stored.Getpoint(4, 4)[0]);

        var (raster, _) = ImageCodec.DecodeForUpscale(tagged);

        var pixel = raster![4, 4];
        Assert.InRange(pixel.R, 198, 202);
        Assert.InRange(pixel.G, 28, 32);
        Assert.InRange(pixel.B, 58, 62);
    }

    [Fact]
    public void Transparency_lands_on_white_when_decoding_for_upscaling()
    {
        var rgba = new Raster(4, 4, new Rgba(10, 20, 30, 0));

        var (raster, _) = ImageCodec.DecodeForUpscale(rgba.EncodePng());

        Assert.Equal(new Rgba(255, 255, 255), raster![1, 1]);
    }

    [Fact]
    public void Decoding_for_upscaling_skips_what_it_cannot_take()
    {
        Assert.Equal(UpscaleReasons.Unreadable, ImageCodec.DecodeForUpscale([1, 2, 3, 4]).SkipReason);

        var jpeg = new Raster(64, 64, new Rgba(200, 10, 10), bands: 3).EncodeJpeg(90);
        Assert.Equal(UpscaleReasons.Unreadable, ImageCodec.DecodeForUpscale(jpeg[..(jpeg.Length / 2)]).SkipReason);

        using var deep = NetVips.Image.Black(8, 8, bands: 3).Cast(NetVips.Enums.BandFormat.Ushort)
            .Copy(interpretation: NetVips.Enums.Interpretation.Rgb16);
        Assert.Equal(UpscaleReasons.HighBitDepth, ImageCodec.DecodeForUpscale(deep.PngsaveBuffer()).SkipReason);

        using var tall = NetVips.Image.Black(1, PageConversion.MaxEdge + 1).Cast(NetVips.Enums.BandFormat.Uchar);
        Assert.Equal(UpscaleReasons.TooLarge, ImageCodec.DecodeForUpscale(tall.PngsaveBuffer()).SkipReason);
    }

    [Fact]
    public void Decoding_for_upscaling_skips_a_page_over_the_server_pixel_limit()
    {
        using var limit = NetVips.Image.Black(4000, 4000).Cast(NetVips.Enums.BandFormat.Uchar);
        using var over = NetVips.Image.Black(4000, 4001).Cast(NetVips.Enums.BandFormat.Uchar);

        Assert.Null(ImageCodec.DecodeForUpscale(limit.PngsaveBuffer(compression: 1)).SkipReason);
        Assert.Equal(UpscaleReasons.TooLarge, ImageCodec.DecodeForUpscale(over.PngsaveBuffer(compression: 1)).SkipReason);
    }

    [Fact]
    public void An_upscaled_page_is_webp_with_one_band_when_grey_and_jpeg_past_the_webp_limit()
    {
        var grey = Enumerable.Repeat((byte)90, 30 * 20 * 3).ToArray();
        var webp = ImageCodec.EncodeUpscaled(grey, 30, 20, grey: true);
        Assert.Equal(("webp", 1), (webp.Extension, webp.Bands));
        Assert.Equal(ImageFormat.Webp, ImageCodec.DetectFormat(webp.Bytes));
        // WebP has no greyscale mode, so it decodes to three equal bands; the saving is the empty chroma.
        using (var decoded = NetVips.Image.NewFromBuffer(webp.Bytes))
        {
            Assert.Equal(3, decoded.Bands);
            Assert.True(UpscalePixels.IsGreyscale(decoded.WriteToMemory<byte>()));
        }

        var colour = new byte[30 * 20 * 3];
        for (var i = 0; i < colour.Length; i += 3) colour[i] = 255;
        var red = ImageCodec.EncodeUpscaled(colour, 30, 20, grey: false);
        Assert.Equal(("webp", 3), (red.Extension, red.Bands));
        // A model's tint on a grey source page is dropped with the chroma.
        var tinted = ImageCodec.EncodeUpscaled(colour, 30, 20, grey: true);
        Assert.Equal(("webp", 1), (tinted.Extension, tinted.Bands));

        var tall = ImageCodec.EncodeUpscaled(new byte[2 * (ImageCodec.MaxWebpEdge + 1) * 3], 2, ImageCodec.MaxWebpEdge + 1, grey: true);
        Assert.Equal(("jpg", 1), (tall.Extension, tall.Bands));
        Assert.Equal(ImageFormat.Jpeg, ImageCodec.DetectFormat(tall.Bytes));
        using var jpeg = NetVips.Image.NewFromBuffer(tall.Bytes);
        Assert.Equal(1, jpeg.Bands);
    }
}
