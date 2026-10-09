using Maki.Api.Configuration;
using Maki.Api.Dtos;
using Maki.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

[Collection(ConfigDirCollection.Name)]
public class CoverVersionTests : IDisposable
{
    private readonly string _configDir =
        Path.Combine(Path.GetTempPath(), "maki-cover-version-" + Guid.NewGuid().ToString("N"));
    private readonly string? _priorEnv = Environment.GetEnvironmentVariable("MAKI_CONFIG_DIR");
    private readonly PngHttpFactory _http = new() { Width = 800, Height = 1200 };
    private readonly CoverService _covers;

    public CoverVersionTests()
    {
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _configDir);
        _covers = new CoverService(_http, new AppPaths(), new FakeAppSettings(), NullLogger<CoverService>.Instance);
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

    private static string Version(string? url) => url!.Split("?v=")[1];

    [Fact]
    public async Task The_version_follows_the_cover_file_not_the_metadata_refresh()
    {
        var path = await _covers.DownloadCoverAsync(1, "https://cdn.test/a.png");
        Assert.NotNull(path);

        var first = Version(SeriesDto.CoverUrlFor(1, path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        var second = Version(SeriesDto.CoverUrlFor(1, path, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)));

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Fetching_the_same_cover_again_keeps_the_version()
    {
        var path = (await _covers.DownloadCoverAsync(1, "https://cdn.test/a.png"))!;
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var before = Version(SeriesDto.CoverUrlFor(1, path));

        await _covers.DownloadCoverAsync(1, "https://cdn.test/a.png");

        Assert.Equal(before, Version(SeriesDto.CoverUrlFor(1, path)));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public async Task A_changed_cover_gets_a_new_version()
    {
        var path = (await _covers.DownloadCoverAsync(1, "https://cdn.test/a.png"))!;
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var before = Version(SeriesDto.CoverUrlFor(1, path));

        _http.Width = 600;
        await _covers.DownloadCoverAsync(1, "https://cdn.test/b.png");

        Assert.NotEqual(before, Version(SeriesDto.CoverUrlFor(1, path)));
    }

    [Fact]
    public void A_missing_file_falls_back_to_the_given_version()
    {
        var stamp = new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc);

        var url = SeriesDto.CoverUrlFor(9, Path.Combine(_configDir, "nope.jpg"), stamp);

        Assert.Equal(stamp.Ticks.ToString(), Version(url));
    }
}
