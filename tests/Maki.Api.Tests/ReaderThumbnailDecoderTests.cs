using Maki.Api.Controllers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Maki.Api.Tests;

public class ReaderThumbnailDecoderTests
{
    private static MemoryStream Encode(int width, int height, bool jpeg)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(120, 60, 200));
        var stream = new MemoryStream();
        if (jpeg)
        {
            image.SaveAsJpeg(stream);
        }
        else
        {
            image.SaveAsPng(stream);
        }

        stream.Position = 0;
        return stream;
    }

    private static Size Thumbnail(Image image)
    {
        image.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(200, 0), Mode = ResizeMode.Max }));
        return image.Size;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Gives_the_same_thumbnail_shape_as_a_full_decode(bool jpeg)
    {
        using var full = Encode(2000, 3000, jpeg);
        using var expected = await Image.LoadAsync(full);

        using var source = Encode(2000, 3000, jpeg);
        using var scaled = await Image.LoadAsync(await ReaderController.ThumbnailDecoderAsync(source, default), source);

        Assert.Equal(Thumbnail(expected), Thumbnail(scaled));
    }

    [Fact]
    public async Task A_jpeg_decodes_at_a_fraction_of_its_size()
    {
        using var source = Encode(2000, 3000, jpeg: true);

        using var image = await Image.LoadAsync(await ReaderController.ThumbnailDecoderAsync(source, default), source);

        Assert.True(image.Width <= 400, $"decoded at {image.Width}x{image.Height}");
    }
}
