using Maki.Api.Configuration;

namespace Maki.Api.Tests;

public class ConfigFileProviderTests : IDisposable
{
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "maki-config-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_configDir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void A_key_in_another_case_is_honoured_and_not_erased()
    {
        var paths = new AppPaths(_configDir);
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(paths.ConfigFile, "{ \"port\": 9123, \"loglevel\": \"Debug\" }");

        var provider = new ConfigFileProvider(paths);

        Assert.Equal(9123, provider.Config.Port);
        Assert.Equal("Debug", provider.Config.LogLevel);
        Assert.Contains("9123", File.ReadAllText(paths.ConfigFile));
        Assert.False(File.Exists(paths.ConfigFile + ".tmp"));
    }

    [Fact]
    public void A_malformed_config_file_is_reported_by_name()
    {
        var paths = new AppPaths(_configDir);
        File.WriteAllText(paths.ConfigFile, "{ \"Port\": 8990, }");

        var ex = Assert.Throws<InvalidOperationException>(() => new ConfigFileProvider(paths));

        Assert.Contains(paths.ConfigFile, ex.Message);
    }
}
