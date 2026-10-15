using Maki.Core.Images;

namespace Maki.Core.Tests;

public class ImageCodecTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("maki-image-codec-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    private static Raster Gradient(int width, int height)
    {
        var image = new Raster(width, height, bands: 3);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            image[x, y] = new Rgba((byte)(x * 255 / width), (byte)(y * 255 / height), (byte)((x + y) % 256));
        return image;
    }

    [Fact]
    public void Detects_every_format_maki_serves_from_its_signature()
    {
        var image = Gradient(40, 30);

        Assert.Equal(ImageFormat.Jpeg, ImageCodec.DetectFormat(image.EncodeJpeg(80)));
        Assert.Equal(ImageFormat.Png, ImageCodec.DetectFormat(image.EncodePng()));
        Assert.Equal(ImageFormat.Webp, ImageCodec.DetectFormat(image.EncodeWebp(80)));
        Assert.Equal(ImageFormat.Webp, ImageCodec.DetectFormat(image.EncodeWebpLossless()));
        Assert.Equal(ImageFormat.Gif, ImageCodec.DetectFormat(image.EncodeGif()));
        Assert.Null(ImageCodec.DetectFormat("<!doctype html>"u8));
    }

    [Fact]
    public void Identify_reads_the_header_and_names_the_format()
    {
        Assert.Equal(new ImageHeader(40, 30, ImageFormat.Webp), ImageCodec.Identify(Gradient(40, 30).EncodeWebp(80)));
    }

    [Fact]
    public void A_tiff_is_refused_because_its_loader_is_blocked()
    {
        using var image = NetVips.Image.Black(8, 8);
        var tiff = image.TiffsaveBuffer();

        Assert.Null(ImageCodec.DetectFormat(tiff));
        Assert.Null(ImageCodec.TryIdentify(tiff));
        Assert.Throws<NetVips.VipsException>(() => Raster.Decode(tiff));
    }

    [Fact]
    public void A_bmp_header_is_read_without_a_decoder()
    {
        var bmp = new byte[54];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.GetBytes(40).CopyTo(bmp, 14);
        BitConverter.GetBytes(640).CopyTo(bmp, 18);
        BitConverter.GetBytes(-480).CopyTo(bmp, 22);

        Assert.Equal(new ImageHeader(640, 480, ImageFormat.Bmp), ImageCodec.Identify(bmp));
        Assert.Equal(ImageFormat.Bmp, ImageCodec.DetectFormat(bmp));
    }

    [Fact]
    public void A_cover_is_shrunk_but_never_enlarged()
    {
        var wide = ImageCodec.Identify(ImageCodec.JpegThumbnail(Gradient(900, 1200).EncodePng(), 400, 90, enlarge: false));
        var narrow = ImageCodec.Identify(ImageCodec.JpegThumbnail(Gradient(300, 450).EncodePng(), 400, 90, enlarge: false));

        Assert.Equal(new ImageHeader(400, 533, ImageFormat.Jpeg), wide);
        Assert.Equal(new ImageHeader(300, 450, ImageFormat.Jpeg), narrow);
    }

    [Fact]
    public void Raster_round_trips_losslessly_through_png()
    {
        var image = Gradient(17, 9);

        var back = Raster.Decode(image.EncodePng(), bands: 3);

        Assert.Equal(image.Pixels, back.Pixels);
    }

    [Fact]
    public void Two_sizes_of_one_page_hash_alike_and_another_page_does_not()
    {
        var page = Gradient(600, 900);
        var small = Path.Combine(_dir, "small.jpg");
        var large = Path.Combine(_dir, "large.png");
        var other = Path.Combine(_dir, "other.jpg");
        File.WriteAllBytes(large, page.EncodePng());
        File.WriteAllBytes(small, ImageCodec.JpegThumbnail(page.EncodePng(), 300, 80, enlarge: false));
        var flipped = new Raster(600, 900, bands: 3);
        for (var y = 0; y < 900; y++)
        for (var x = 0; x < 600; x++)
            flipped[x, y] = page[599 - x, 899 - y];
        File.WriteAllBytes(other, flipped.EncodeJpeg(80));

        var a = PerceptualHash.OfFile(large);
        var b = PerceptualHash.OfFile(small);
        var c = PerceptualHash.OfFile(other);

        Assert.True(PerceptualHash.Distance(a, b) <= 4, $"same page differs by {PerceptualHash.Distance(a, b)} bits");
        Assert.True(PerceptualHash.Distance(a, c) > 20, $"different pages differ by only {PerceptualHash.Distance(a, c)} bits");
    }
}
