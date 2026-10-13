using Maki.Api.Configuration;
using Maki.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;

namespace Maki.Api.Tests;

[Collection(ConfigDirCollection.Name)]
public class CoverServiceStoreTests : IDisposable
{
    private readonly string _configDir =
        Path.Combine(Path.GetTempPath(), "maki-cover-store-" + Guid.NewGuid().ToString("N"));
    private readonly string? _priorEnv = Environment.GetEnvironmentVariable("MAKI_CONFIG_DIR");
    private readonly CoverService _covers;

    public CoverServiceStoreTests()
    {
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _configDir);
        _covers = new CoverService(null!, new AppPaths(), new FakeAppSettings(), NullLogger<CoverService>.Instance);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _priorEnv);
        try
        {
            Directory.Delete(_configDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static MemoryStream AnimatedGif(int frames, Rgba32 first)
    {
        using var image = new Image<Rgba32>(600, 800, first);
        for (var i = 1; i < frames; i++)
        {
            using var frame = new Image<Rgba32>(600, 800, new Rgba32((byte)(i * 20), 0, 0));
            image.Frames.AddFrame(frame.Frames.RootFrame);
        }

        var stream = new MemoryStream();
        image.SaveAsGif(stream, new GifEncoder());
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public async Task An_animated_upload_becomes_a_poster_from_its_first_frame()
    {
        await using var gif = AnimatedGif(6, new Rgba32(0, 200, 0));

        var path = await _covers.StoreUploadedCoverAsync(5, gif);

        using var poster = await Image.LoadAsync<Rgba32>(path);
        Assert.Equal(1, poster.Frames.Count);
        Assert.Equal(400, poster.Width);
        var pixel = poster[10, 10];
        Assert.True(pixel.G > 150 && pixel.R < 60, $"expected the green first frame, got {pixel}");
    }

    [Fact]
    public async Task Concurrent_uploads_for_one_series_do_not_collide_on_the_temp_file()
    {
        var uploads = Enumerable.Range(0, 8).Select(async i =>
        {
            await using var gif = AnimatedGif(1, new Rgba32((byte)(i * 30), 100, 100));
            return await _covers.StoreUploadedCoverAsync(9, gif);
        });

        var paths = await Task.WhenAll(uploads);

        Assert.All(paths, p => Assert.Equal(_covers.CoverPathFor(9), p));
        Assert.Equal(["cover.jpg"], Directory.GetFiles(Path.GetDirectoryName(paths[0])!).Select(f => Path.GetFileName(f)));
    }
}
