using Maki.Api.Configuration;
using Maki.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Maki.Core.Images;

namespace Maki.Api.Tests;

public class CoverServiceStoreTests : IDisposable
{
    private readonly string _configDir =
        Path.Combine(Path.GetTempPath(), "maki-cover-store-" + Guid.NewGuid().ToString("N"));
    private readonly CoverService _covers;

    public CoverServiceStoreTests()
    {
        _covers = new CoverService(null!, new AppPaths(_configDir), new FakeAppSettings(), NullLogger<CoverService>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_configDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Frames stacked top to bottom with a page height, which is how libvips writes an animation.</summary>
    private static MemoryStream AnimatedGif(int frames, Rgba first)
    {
        var strip = new Raster(600, 800 * frames, bands: 3);
        for (var i = 0; i < frames; i++)
        {
            var colour = i == 0 ? first : new Rgba((byte)(i * 20), 0, 0);
            for (var y = 0; y < 800; y++)
            for (var x = 0; x < 600; x++)
                strip[x, i * 800 + y] = colour;
        }

        using var image = NetVips.Image.NewFromMemory(strip.Pixels, strip.Width, strip.Height, 3, NetVips.Enums.BandFormat.Uchar);
        using var srgb = image.Copy(interpretation: NetVips.Enums.Interpretation.Srgb);
        using var paged = srgb.Mutate(m => m.Set(NetVips.GValue.GIntType, "page-height", 800));
        return new MemoryStream(paged.GifsaveBuffer());
    }

    [Fact]
    public async Task An_animated_upload_becomes_a_poster_from_its_first_frame()
    {
        await using var gif = AnimatedGif(6, new Rgba(0, 200, 0));

        var path = await _covers.StoreUploadedCoverAsync(5, gif);

        var poster = Raster.Decode(await File.ReadAllBytesAsync(path));
        Assert.Equal(ImageFormat.Jpeg, ImageCodec.IdentifyFile(path).Format);
        Assert.Equal(400, poster.Width);
        Assert.Equal(533, poster.Height);
        var pixel = poster[10, 10];
        Assert.True(pixel.G > 150 && pixel.R < 60, $"expected the green first frame, got {pixel}");
    }

    [Fact]
    public async Task Concurrent_uploads_for_one_series_do_not_collide_on_the_temp_file()
    {
        var uploads = Enumerable.Range(0, 8).Select(async i =>
        {
            await using var gif = AnimatedGif(1, new Rgba((byte)(i * 30), 100, 100));
            return await _covers.StoreUploadedCoverAsync(9, gif);
        });

        var paths = await Task.WhenAll(uploads);

        Assert.All(paths, p => Assert.Equal(_covers.CoverPathFor(9), p));
        Assert.Equal(
            ["cover-sm.jpg", "cover.jpg"],
            Directory.GetFiles(Path.GetDirectoryName(paths[0])!).Select(f => Path.GetFileName(f)).Order());
    }

    [Fact]
    public async Task Storing_a_poster_also_writes_the_240_px_one()
    {
        await using var gif = AnimatedGif(1, new Rgba(0, 200, 0));

        await _covers.StoreUploadedCoverAsync(11, gif);

        var small = _covers.SmallCoverPathFor(11);
        Assert.Equal(ImageFormat.Jpeg, ImageCodec.IdentifyFile(small).Format);
        Assert.Equal(240, Raster.Decode(await File.ReadAllBytesAsync(small)).Width);
    }

    [Fact]
    public async Task A_cover_without_a_small_one_gets_it_made_on_first_request()
    {
        await using var gif = AnimatedGif(1, new Rgba(0, 200, 0));
        await _covers.StoreUploadedCoverAsync(12, gif);
        File.Delete(_covers.SmallCoverPathFor(12));

        var path = await _covers.EnsureSmallCoverAsync(12);

        Assert.Equal(_covers.SmallCoverPathFor(12), path);
        Assert.Equal(240, Raster.Decode(await File.ReadAllBytesAsync(path!)).Width);
    }

    [Fact]
    public async Task A_small_cover_older_than_its_poster_is_remade()
    {
        await using var gif = AnimatedGif(1, new Rgba(0, 200, 0));
        await _covers.StoreUploadedCoverAsync(13, gif);
        var small = _covers.SmallCoverPathFor(13);
        File.SetLastWriteTimeUtc(small, File.GetLastWriteTimeUtc(_covers.CoverPathFor(13)).AddMinutes(-5));
        var stale = File.GetLastWriteTimeUtc(small);

        await _covers.EnsureSmallCoverAsync(13);

        Assert.True(File.GetLastWriteTimeUtc(small) > stale);
    }

    [Fact]
    public async Task No_small_cover_exists_for_a_series_without_a_poster()
    {
        Assert.Null(await _covers.EnsureSmallCoverAsync(14));
    }
}
