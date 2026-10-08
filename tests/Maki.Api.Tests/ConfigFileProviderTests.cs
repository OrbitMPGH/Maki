using Maki.Api.Configuration;

namespace Maki.Api.Tests;

[Collection(ConfigDirCollection.Name)]
public class ConfigFileProviderTests : IDisposable
{
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "maki-config-tests", Guid.NewGuid().ToString("N"));
    private readonly string? _priorEnv = Environment.GetEnvironmentVariable("MAKI_CONFIG_DIR");

    public ConfigFileProviderTests() => Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _configDir);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _priorEnv);
        try
        {
            Directory.Delete(_configDir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void A_malformed_config_file_is_reported_by_name()
    {
        var paths = new AppPaths();
        File.WriteAllText(paths.ConfigFile, "{ \"Port\": 8990, }");

        var ex = Assert.Throws<InvalidOperationException>(() => new ConfigFileProvider(paths));

        Assert.Contains(paths.ConfigFile, ex.Message);
    }
}
