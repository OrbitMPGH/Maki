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
        Assert.Equal(3 * 4, tiles.Count);
        Assert.Equal(new UpscaleTile(768, 1152, 132, 148, 16, 164, 180, 0), tiles[^1]);
    }

    [Theory]
    [InlineData(384, 416)]
    [InlineData(105, 140)]
    [InlineData(1, 36)]
    public void The_context_tile_rounds_up_to_a_multiple_of_four(int interior, int context)
    {
        var tile = UpscaleTiling.Plan(interior, interior, multiple: 4).Single();

        Assert.Equal(context, tile.ContextWidth);
        Assert.Equal(context, tile.ContextHeight);
        Assert.Equal(0, tile.ContextWidth % 4);
        Assert.Equal(context, tile.InputWidth);
    }

    [Theory]
    [InlineData(384)]
    [InlineData(105)]
    [InlineData(1)]
    public void The_waifu2x_border_makes_its_shrunk_output_exactly_twice_the_context_tile(int interior)
    {
        var tile = UpscaleTiling.Plan(interior, interior, multiple: 4, border: UpscaleTiling.Waifu2xBorder).Single();

        Assert.Equal(tile.ContextWidth + 16, tile.InputWidth);
        var waifu2xOutput = 2 * tile.InputWidth - 32;
        Assert.Equal(2 * tile.ContextWidth, waifu2xOutput);
        Assert.True(2 * tile.Pad + 2 * interior <= waifu2xOutput);
    }

    [Fact]
    public void The_waifu2x_border_reflects_the_context_tile_not_the_page()
    {
        var tile = UpscaleTiling.Plan(1000, 1000, multiple: 4, border: UpscaleTiling.Waifu2xBorder)[1];
        Assert.Equal(384, tile.X);

        // Input column 0 is 8 px left of the context tile, mirrored inside it: context column 8.
        Assert.Equal(384 - 16 + 8, tile.SourceX(0, 1000));
        Assert.Equal(384 - 16, tile.SourceX(8, 1000));
        Assert.Equal(384 - 16 + tile.ContextWidth - 1 - 8, tile.SourceX(tile.InputWidth - 1, 1000));
    }

    [Fact]
    public void Context_past_the_page_edge_is_reflected_from_the_page()
    {
        var tile = UpscaleTiling.Plan(100, 100, multiple: 4).Single();

        Assert.Equal(16, tile.SourceX(0, 100));
        Assert.Equal(0, tile.SourceX(16, 100));
        Assert.Equal(99, tile.SourceX(115, 100));
        Assert.Equal(98, tile.SourceX(116, 100));
        Assert.Equal(132, tile.ContextWidth);
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
    public void An_upscaled_page_is_webp_with_one_band_when_grey_and_jpeg_past_the_webp_limit()
    {
        var grey = Enumerable.Repeat((byte)90, 30 * 20 * 3).ToArray();
        var webp = ImageCodec.EncodeUpscaled(grey, 30, 20);
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
        var red = ImageCodec.EncodeUpscaled(colour, 30, 20);
        Assert.Equal(("webp", 3), (red.Extension, red.Bands));

        var tall = ImageCodec.EncodeUpscaled(new byte[2 * (ImageCodec.MaxWebpEdge + 1) * 3], 2, ImageCodec.MaxWebpEdge + 1);
        Assert.Equal(("jpg", 1), (tall.Extension, tall.Bands));
        Assert.Equal(ImageFormat.Jpeg, ImageCodec.DetectFormat(tall.Bytes));
        using var jpeg = NetVips.Image.NewFromBuffer(tall.Bytes);
        Assert.Equal(1, jpeg.Bands);
    }
}
