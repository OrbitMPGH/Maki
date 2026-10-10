using Maki.Api.Configuration;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

[Collection(ConfigDirCollection.Name)]
public class CoverServiceLibraryCoverTests : IDisposable
{
    private readonly string _configDir =
        Path.Combine(Path.GetTempPath(), "maki-library-cover-" + Guid.NewGuid().ToString("N"));
    private readonly string? _priorEnv = Environment.GetEnvironmentVariable("MAKI_CONFIG_DIR");
    private readonly AppPaths _paths;
    private readonly CoverService _covers;
    private readonly string _seriesFolder;

    public CoverServiceLibraryCoverTests()
    {
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _configDir);
        _paths = new AppPaths();
        _covers = new CoverService(null!, _paths,
            new FakeAppSettings().Set(SettingKeys.LibraryWriteCoverToFolder, "true"),
            NullLogger<CoverService>.Instance);
        _seriesFolder = Path.Combine(_configDir, "library", "Series");
        Directory.CreateDirectory(_seriesFolder);
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

    private string FolderCover => Path.Combine(_seriesFolder, "cover.jpg");

    private void SetPoster(int seriesId, byte[] bytes)
    {
        var path = _covers.CoverPathFor(seriesId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    [Fact]
    public async Task A_cover_the_user_placed_in_the_folder_survives()
    {
        File.WriteAllBytes(FolderCover, [9, 9, 9]);
        SetPoster(1, [1, 2, 3]);

        await _covers.WriteLibraryCoverAsync(1, _seriesFolder);

        Assert.Equal([9, 9, 9], File.ReadAllBytes(FolderCover));
    }

    [Fact]
    public async Task A_cover_Maki_wrote_is_refreshed_when_the_poster_changes()
    {
        SetPoster(1, [1, 2, 3]);
        await _covers.WriteLibraryCoverAsync(1, _seriesFolder);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(FolderCover));

        SetPoster(1, [4, 5, 6]);
        await _covers.WriteLibraryCoverAsync(1, _seriesFolder);

        Assert.Equal([4, 5, 6], File.ReadAllBytes(FolderCover));
    }

    [Fact]
    public async Task A_cover_the_user_replaced_after_Maki_wrote_one_survives()
    {
        SetPoster(1, [1, 2, 3]);
        await _covers.WriteLibraryCoverAsync(1, _seriesFolder);
        File.WriteAllBytes(FolderCover, [9, 9, 9]);

        SetPoster(1, [4, 5, 6]);
        await _covers.WriteLibraryCoverAsync(1, _seriesFolder);

        Assert.Equal([9, 9, 9], File.ReadAllBytes(FolderCover));
    }
}
