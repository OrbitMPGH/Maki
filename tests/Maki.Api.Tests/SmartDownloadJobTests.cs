using Maki.Api.Jobs;
using Maki.Core.Configuration;
using Maki.Core.Entities;

namespace Maki.Api.Tests;

/// <summary>
/// <see cref="SmartDownloadJob.MonitorSmart(List{Chapter}, IAppSettings, CancellationToken)"/> is the
/// pure selection/monitoring step shared by the controller (initial pick when switching to Smart) and
/// the job (topping up as chapters get read). It must cap monitoring to the next batch (plus whatever's
/// already downloaded) instead of just adding to whatever was monitored before.
/// </summary>
public class SmartDownloadJobTests
{
    private static Chapter Ch(int id, decimal number, int? fileId = null, bool monitored = false) =>
        new() { Id = id, Number = number, Language = "en", Monitored = monitored, ChapterFileId = fileId };

    private static async Task<HashSet<int>> Run(List<Chapter> chapters, FakeAppSettings settings) =>
        await SmartDownloadJob.MonitorSmart(chapters, settings, CancellationToken.None);

    [Fact]
    public async Task Picks_next_undownloaded_chapters_up_to_the_configured_count()
    {
        var settings = new FakeAppSettings().Set(SettingKeys.SmartDownloadChaptersCount, "2");
        var chapters = new List<Chapter> { Ch(1, 1m, fileId: 10), Ch(2, 2m), Ch(3, 3m), Ch(4, 4m) };

        var missing = await Run(chapters, settings);

        Assert.Equal([2, 3], missing.Order());
        Assert.True(chapters.Single(c => c.Id == 2).Monitored);
        Assert.True(chapters.Single(c => c.Id == 3).Monitored);
        Assert.False(chapters.Single(c => c.Id == 4).Monitored);
    }

    [Fact]
    public async Task Unmonitors_undownloaded_chapters_outside_the_smart_window()
    {
        var settings = new FakeAppSettings().Set(SettingKeys.SmartDownloadChaptersCount, "1");
        // Simulate switching from "All" mode: everything starts monitored.
        var chapters = new List<Chapter>
        {
            Ch(1, 1m, fileId: 10, monitored: true),
            Ch(2, 2m, monitored: true),
            Ch(3, 3m, monitored: true),
        };

        await Run(chapters, settings);

        Assert.True(chapters.Single(c => c.Id == 2).Monitored);
        Assert.False(chapters.Single(c => c.Id == 3).Monitored);
    }

    [Fact]
    public async Task Skips_specials_when_unmonitor_specials_setting_is_on()
    {
        var settings = new FakeAppSettings()
            .Set(SettingKeys.SmartDownloadChaptersCount, "5")
            .Set(SettingKeys.MonitoringUnmonitorSpecials, "true");
        var chapters = new List<Chapter> { Ch(1, 1m), Ch(2, 1.5m), Ch(3, 2m) };

        var missing = await Run(chapters, settings);

        Assert.Equal([1, 3], missing.Order());
        Assert.False(chapters.Single(c => c.Id == 2).Monitored);
    }

    [Fact]
    public async Task Already_downloaded_chapters_are_never_selected_but_stay_monitored()
    {
        var settings = new FakeAppSettings().Set(SettingKeys.SmartDownloadChaptersCount, "10");
        var chapters = new List<Chapter> { Ch(1, 1m, fileId: 10, monitored: true), Ch(2, 2m) };

        var missing = await Run(chapters, settings);

        Assert.Equal([2], missing);
        Assert.True(chapters.Single(c => c.Id == 1).Monitored);
    }
}
