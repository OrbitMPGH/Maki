using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;

namespace Maki.Api.Tests;

public sealed class MonitorDefaultsTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private Task<NewChapterMonitorMode> Resolve(params (string Key, string Value)[] config)
    {
        _db.SetConfig(config);
        return MonitorDefaults.ForNewSeriesAsync(new SettingsService(_db.ScopeFactory()), CancellationToken.None);
    }

    [Fact]
    public async Task Unset_is_All_as_it_always_was() =>
        Assert.Equal(NewChapterMonitorMode.All, await Resolve());

    [Fact]
    public async Task The_setting_picks_the_mode() =>
        Assert.Equal(NewChapterMonitorMode.Smart, await Resolve((SettingKeys.MonitoringDefaultMode, "Smart")));

    [Fact]
    public async Task Skipping_specials_narrows_All_but_leaves_Smart_alone()
    {
        Assert.Equal(NewChapterMonitorMode.MainOnly,
            await Resolve((SettingKeys.MonitoringUnmonitorSpecials, "true")));
        Assert.Equal(NewChapterMonitorMode.Smart,
            await Resolve((SettingKeys.MonitoringUnmonitorSpecials, "true"), (SettingKeys.MonitoringDefaultMode, "Smart")));
    }

    /// <summary>"None" would contradict a monitoring switch that is on, so a stored one reads as All.</summary>
    [Fact]
    public async Task None_or_garbage_falls_back_to_All()
    {
        Assert.Equal(NewChapterMonitorMode.All, await Resolve((SettingKeys.MonitoringDefaultMode, "None")));
        Assert.Equal(NewChapterMonitorMode.All, await Resolve((SettingKeys.MonitoringDefaultMode, "lots")));
    }
}
