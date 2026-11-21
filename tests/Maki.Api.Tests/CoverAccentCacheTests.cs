using Maki.Api.Configuration;
using Maki.Api.Services;
using Maki.Core.Images;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>The library list's cached-only path: never decodes in the request, fills in behind it.</summary>
public sealed class CoverAccentCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maki-tests", "cover-accent-" + Guid.NewGuid().ToString("N"));
    private readonly CoverService _covers;
    private readonly CoverAccentCache _accents;

    public CoverAccentCacheTests()
    {
        _covers = new CoverService(null!, new AppPaths(_root), new FakeAppSettings(), NullLogger<CoverService>.Instance);
        _accents = new CoverAccentCache(_covers, NullLogger<CoverAccentCache>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void WriteRedCover(int seriesId)
    {
        var path = _covers.CoverPathFor(seriesId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new Raster(60, 84, new Rgba(190, 28, 46), bands: 3).EncodeJpeg(90));
    }

    [Fact]
    public void NoCoverHasNoColour() => Assert.Null(_accents.CachedAccentFor(41));

    [Fact]
    public async Task FirstAskQueuesAndALaterAskHasTheColour()
    {
        WriteRedCover(7);

        Assert.Null(_accents.CachedAccentFor(7));

        string? hex = null;
        for (var i = 0; i < 100 && hex is null; i++)
        {
            await Task.Delay(50);
            hex = _accents.CachedAccentFor(7);
        }

        Assert.NotNull(hex);
        Assert.Equal(_accents.AccentFor(7), hex);
    }
}
