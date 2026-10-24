using Maki.Api.Controllers;
using Maki.Core.Images;

namespace Maki.Api.Tests;

public class ReaderThumbnailTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_page_becomes_a_200px_jpeg_with_its_own_shape(bool jpeg)
    {
        var page = new Raster(2000, 3000, new Rgba(120, 60, 200), bands: 3);

        var thumbnail = ImageCodec.Identify(ReaderController.ReaderThumbnail(jpeg ? page.EncodeJpeg(75) : page.EncodePng()));

        Assert.Equal(new ImageHeader(200, 300, ImageFormat.Jpeg), thumbnail);
    }

    [Fact]
    public void A_narrower_page_is_scaled_up_so_the_strip_stays_even()
    {
        var thumbnail = ImageCodec.Identify(ReaderController.ReaderThumbnail(new Raster(150, 300).EncodePng()));

        Assert.Equal(200, thumbnail.Width);
        Assert.Equal(400, thumbnail.Height);
    }

    [Fact]
    public void Transparency_lands_on_white_not_black()
    {
        var page = new Raster(400, 600, new Rgba(0, 0, 0, 0));

        var thumbnail = Raster.Decode(ReaderController.ReaderThumbnail(page.EncodePng()), bands: 3);

        var pixel = thumbnail[100, 150];
        Assert.True(pixel.R > 245 && pixel.G > 245 && pixel.B > 245, $"expected white, got {pixel}");
    }
}
