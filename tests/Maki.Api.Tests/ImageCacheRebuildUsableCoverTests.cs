using Maki.Api.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats.Jpeg;

namespace Maki.Api.Tests;

public class ImageCacheRebuildUsableCoverTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "maki-usable-cover-" + Guid.NewGuid().ToString("N")[..8]);

    public ImageCacheRebuildUsableCoverTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteJpeg()
    {
        var path = Path.Combine(_dir, "cover.jpg");
        var rng = new Random(1);
        using var image = new Image<Rgb24>(200, 300);
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                image[x, y] = new Rgb24((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
            }
        }

        image.SaveAsJpeg(path, new JpegEncoder { Quality = 90 });
        return path;
    }

    [Fact]
    public void AWholeJpegIsUsable() => Assert.True(ImageCacheRebuildService.IsUsableCover(WriteJpeg()));

    [Fact]
    public void AMissingOrEmptyFileIsNotUsable()
    {
        Assert.False(ImageCacheRebuildService.IsUsableCover(Path.Combine(_dir, "nope.jpg")));
        var empty = Path.Combine(_dir, "empty.jpg");
        File.WriteAllBytes(empty, []);
        Assert.False(ImageCacheRebuildService.IsUsableCover(empty));
    }

    [Fact]
    public void AJpegCutOffMidwayIsNotUsable()
    {
        var path = WriteJpeg();
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);

        Assert.False(ImageCacheRebuildService.IsUsableCover(path));
    }
}
