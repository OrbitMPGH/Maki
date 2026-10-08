using Maki.Api.Configuration;
using Maki.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

[Collection(ConfigDirCollection.Name)]
public class CoverServiceDeleteTests : IDisposable
{
    private readonly string _configDir =
        Path.Combine(Path.GetTempPath(), "maki-cover-delete-" + Guid.NewGuid().ToString("N"));
    private readonly string? _priorEnv = Environment.GetEnvironmentVariable("MAKI_CONFIG_DIR");
    private readonly AppPaths _paths;

    public CoverServiceDeleteTests()
    {
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _configDir);
        _paths = new AppPaths();
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

    private CoverService Covers() =>
        new(null!, _paths, new FakeAppSettings(), NullLogger<CoverService>.Instance);

    [Fact]
    public void DeleteCover_removes_the_poster_folder()
    {
        var covers = Covers();
        var path = covers.CoverPathFor(7);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);

        covers.DeleteCover(7);

        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [Fact]
    public void DeleteCover_swallows_a_folder_that_cannot_be_removed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var covers = Covers();
        var path = covers.CoverPathFor(8);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var held = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        covers.DeleteCover(8);

        Assert.True(File.Exists(path));
    }
}
