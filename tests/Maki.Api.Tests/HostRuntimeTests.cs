using Maki.Api.Configuration;

namespace Maki.Api.Tests;

public class HostRuntimeTests
{
    [Theory]
    [InlineData("docker", false, HostRuntimeKind.Docker)]
    [InlineData("Desktop", false, HostRuntimeKind.Desktop)]
    [InlineData(null, true, HostRuntimeKind.Docker)]
    [InlineData(null, false, HostRuntimeKind.Bare)]
    [InlineData("something-else", false, HostRuntimeKind.Bare)]
    public void Runtime_comes_from_MAKI_RUNTIME_with_dockerenv_as_the_fallback(
        string? variable, bool dockerEnv, HostRuntimeKind expected) =>
        Assert.Equal(expected, HostRuntime.Detect(variable, dockerEnv));

    [Theory]
    [InlineData("*", "http://*:8990")]
    [InlineData("", "http://*:8990")]
    [InlineData("localhost", "http://localhost:8990")]
    [InlineData("LocalHost", "http://localhost:8990")]
    [InlineData("192.168.1.20", "http://192.168.1.20:8990")]
    [InlineData("::1", "http://[::1]:8990")]
    public void Bind_address_becomes_the_listen_url(string bindAddress, string expected) =>
        Assert.Equal(expected, ConfigFileProvider.ListenUrl(new ConfigFile { BindAddress = bindAddress }));

    [Fact]
    public void A_bind_address_that_is_not_an_address_stops_startup_with_a_reason()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConfigFileProvider.ListenUrl(new ConfigFile { BindAddress = "my-laptop" }));

        Assert.Contains("BindAddress", ex.Message);
    }

    [Fact]
    public void Changing_config_on_disk_leaves_the_running_values_alone()
    {
        var dir = Path.Combine(Path.GetTempPath(), "maki-config-" + Guid.NewGuid().ToString("N"));
        try
        {
            var provider = new ConfigFileProvider(new AppPaths(dir));
            var running = provider.Config.BindAddress;

            provider.UpdateOnDisk(c => c.BindAddress = "localhost");

            Assert.Equal(running, provider.Config.BindAddress);
            Assert.Equal("localhost", provider.ReadOnDisk().BindAddress);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
