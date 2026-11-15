using Maki.Api.Services;
using Maki.Core.Configuration;

namespace Maki.Api.Tests;

public sealed class VersionHistoryTests
{
    [Fact]
    public async Task Fresh_install_records_no_previous_version()
    {
        var settings = new FakeAppSettings();
        await VersionHistory.RecordAsync(settings, "0.37.0", freshDatabase: true);

        Assert.Null(await settings.GetAsync(SettingKeys.VersionUpgradedFrom));
        Assert.Equal("0.37.0", await settings.GetAsync(SettingKeys.VersionRunning));
    }

    [Fact]
    public async Task Install_from_before_tracking_counts_as_upgraded_from_zero()
    {
        var settings = new FakeAppSettings();
        await VersionHistory.RecordAsync(settings, "0.37.0", freshDatabase: false);

        Assert.Equal(VersionHistory.BeforeTracking, await settings.GetAsync(SettingKeys.VersionUpgradedFrom));
    }

    [Fact]
    public async Task Upgrade_records_the_previous_version()
    {
        var settings = new FakeAppSettings().Set(SettingKeys.VersionRunning, "0.36.0");
        await VersionHistory.RecordAsync(settings, "0.37.0", freshDatabase: false);

        Assert.Equal("0.36.0", await settings.GetAsync(SettingKeys.VersionUpgradedFrom));
        Assert.Equal("0.37.0", await settings.GetAsync(SettingKeys.VersionRunning));
    }

    [Fact]
    public async Task Restart_on_the_same_version_keeps_the_previous_version()
    {
        var settings = new FakeAppSettings()
            .Set(SettingKeys.VersionRunning, "0.37.0")
            .Set(SettingKeys.VersionUpgradedFrom, "0.36.0");
        await VersionHistory.RecordAsync(settings, "0.37.0", freshDatabase: false);

        Assert.Equal("0.36.0", await settings.GetAsync(SettingKeys.VersionUpgradedFrom));
    }

    [Fact]
    public async Task Upgrade_after_a_fresh_install_records_the_first_version()
    {
        var settings = new FakeAppSettings();
        await VersionHistory.RecordAsync(settings, "0.37.0", freshDatabase: true);
        await VersionHistory.RecordAsync(settings, "0.38.0", freshDatabase: false);

        Assert.Equal("0.37.0", await settings.GetAsync(SettingKeys.VersionUpgradedFrom));
    }

    [Fact]
    public async Task Leaving_a_dev_build_keeps_the_previous_version()
    {
        var settings = new FakeAppSettings()
            .Set(SettingKeys.VersionRunning, "0.0.0-dev")
            .Set(SettingKeys.VersionUpgradedFrom, "0.36.0");
        await VersionHistory.RecordAsync(settings, "0.37.0", freshDatabase: false);

        Assert.Equal("0.36.0", await settings.GetAsync(SettingKeys.VersionUpgradedFrom));
        Assert.Equal("0.37.0", await settings.GetAsync(SettingKeys.VersionRunning));
    }

    [Fact]
    public async Task Downgrade_keeps_the_previous_version()
    {
        var settings = new FakeAppSettings()
            .Set(SettingKeys.VersionRunning, "0.37.0")
            .Set(SettingKeys.VersionUpgradedFrom, "0.36.0");
        await VersionHistory.RecordAsync(settings, "0.36.1", freshDatabase: false);

        Assert.Equal("0.36.0", await settings.GetAsync(SettingKeys.VersionUpgradedFrom));
        Assert.Equal("0.36.1", await settings.GetAsync(SettingKeys.VersionRunning));
    }

    [Fact]
    public async Task Prerelease_to_its_release_counts_as_an_upgrade()
    {
        var settings = new FakeAppSettings().Set(SettingKeys.VersionRunning, "0.37.0-beta.1");
        await VersionHistory.RecordAsync(settings, "0.37.0", freshDatabase: false);

        Assert.Equal("0.37.0-beta.1", await settings.GetAsync(SettingKeys.VersionUpgradedFrom));
    }
}
