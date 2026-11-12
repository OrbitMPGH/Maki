using Maki.Api.Controllers;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Sources;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public class ScanlationGroupsControllerTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeAppSettings _settings = new();

    public void Dispose() => _db.Dispose();

    private ScanlationGroupsController Controller(ISource source) => new(
        _db.NewContext(), _settings, new SourceRegistry([source]), Sources.AllEnabled, new TestLocalizer(),
        NullLogger<ScanlationGroupsController>.Instance);

    private int Seed() => _db.SeedSeries(mappings: new SourceMapping
    {
        SourceName = "multi", SourceSeriesId = "series", Url = "https://multi.test/series", Enabled = true
    });

    [Fact]
    public async Task The_series_view_lists_every_group_with_an_upload_and_the_global_lists()
    {
        _settings.Set(SettingKeys.ScanlationGroups, """{"preferred":["Moonlit TL"],"blocked":["Rushjob"]}""");
        var source = new FakeGroupSource()
            .Add("1-a", 1, "Early Birds", 1)
            .Add("1-b", 1, "Moonlit TL, Paper Lantern", 2)
            .Add("2-a", 2, "Early Birds", 3);
        var seriesId = Seed();

        var result = Assert.IsType<OkObjectResult>(await Controller(source).GetSeries(seriesId, CancellationToken.None));
        var groups = Assert.IsType<ScanlationGroupsController.SeriesGroups>(result.Value);

        Assert.Equal(["Early Birds", "Moonlit TL", "Paper Lantern"], groups.Seen.Select(g => g.Name));
        Assert.Equal(2, groups.Seen[0].Chapters);
        Assert.Equal(["Moonlit TL"], groups.Global.Preferred);
        Assert.Equal(["Rushjob"], groups.Global.Blocked);
        Assert.Equal(["Multi"], groups.Sources);
        Assert.Empty(groups.Preferred);
    }

    [Fact]
    public async Task Saving_a_series_keeps_cleaned_lists()
    {
        var seriesId = Seed();

        await Controller(new FakeGroupSource()).SetSeries(seriesId,
            new ScanlationGroupsController.GroupLists([" Moonlit TL ", "moonlit tl", ""], ["Rushjob"]), CancellationToken.None);

        using var check = _db.NewContext();
        var series = check.Series.Single(s => s.Id == seriesId);
        Assert.Equal(["Moonlit TL"], series.PreferredGroups);
        Assert.Equal(["Rushjob"], series.BlockedGroups);
    }

    [Fact]
    public async Task Saving_empty_global_lists_clears_the_setting()
    {
        var controller = Controller(new FakeGroupSource());
        await controller.SetGlobal(new ScanlationGroupsController.GroupLists(["A"], []), CancellationToken.None);
        Assert.NotNull(await _settings.GetAsync(SettingKeys.ScanlationGroups));

        await controller.SetGlobal(new ScanlationGroupsController.GroupLists([], []), CancellationToken.None);

        Assert.Null(await _settings.GetAsync(SettingKeys.ScanlationGroups));
    }
}
