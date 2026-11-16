using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>A source listing several groups' uploads links the one the global and series group lists pick.</summary>
public class ChapterSyncScanlationGroupTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeAppSettings _settings = new();

    public void Dispose() => _db.Dispose();

    private ChapterSyncService Service(ISource source) => new(
        _db.NewContext(),
        new SourceRegistry([source]),
        new DownloadQueueService(null!, TimeProvider.System, null!, NullLogger<DownloadQueueService>.Instance),
        Sources.AllEnabled,
        new SourceChapterListCache(TimeProvider.System, NullLogger<SourceChapterListCache>.Instance),
        _settings,
        NullLogger<ChapterSyncService>.Instance);

    private int Seed(Action<Series>? series = null)
    {
        var id = _db.SeedSeries(mappings: new SourceMapping
        {
            SourceName = "multi", SourceSeriesId = "series", Url = "https://multi.test/series", Enabled = true
        });
        if (series is not null)
        {
            using var db = _db.NewContext();
            series(db.Series.Single(s => s.Id == id));
            db.SaveChanges();
        }

        return id;
    }

    private Dictionary<decimal, (string Id, string? Group)> Links(int seriesId)
    {
        using var db = _db.NewContext();
        return db.ChapterSourceLinks.Include(l => l.Chapter)
            .Where(l => l.Chapter!.SeriesId == seriesId)
            .ToList()
            .ToDictionary(l => l.Chapter!.Number!.Value, l => (l.SourceChapterId, l.Group));
    }

    private static FakeGroupSource TwoGroups() => new FakeGroupSource()
        .Add("1-early", 1, "Early Birds", 1)
        .Add("1-moon", 1, "Moonlit TL", 3)
        .Add("2-early", 2, "Early Birds", 5);

    [Fact]
    public async Task With_no_lists_the_earliest_upload_is_linked()
    {
        var seriesId = Seed();

        await Service(TwoGroups()).SyncSeriesAsync(seriesId);

        Assert.Equal(("1-early", "Early Birds"), Links(seriesId)[1]);
    }

    [Fact]
    public async Task The_global_preferred_group_is_linked_when_it_has_an_upload()
    {
        _settings.Set(SettingKeys.ScanlationGroups, """{"preferred":["Moonlit TL"],"blocked":[]}""");
        var seriesId = Seed();

        await Service(TwoGroups()).SyncSeriesAsync(seriesId);

        var links = Links(seriesId);
        Assert.Equal("1-moon", links[1].Id);
        Assert.Equal("2-early", links[2].Id);
    }

    [Fact]
    public async Task A_series_list_replaces_the_global_preferred_one()
    {
        _settings.Set(SettingKeys.ScanlationGroups, """{"preferred":["Moonlit TL"]}""");
        var seriesId = Seed(s => s.PreferredGroups = ["Early Birds"]);

        await Service(TwoGroups()).SyncSeriesAsync(seriesId);

        Assert.Equal("1-early", Links(seriesId)[1].Id);
    }

    [Fact]
    public async Task A_chapter_only_a_blocked_group_released_is_not_linked_and_not_marked_delisted()
    {
        var seriesId = Seed();
        var source = TwoGroups();
        await Service(source).SyncSeriesAsync(seriesId);

        using (var db = _db.NewContext())
        {
            var file = new ChapterFile { SeriesId = seriesId, RelativePath = "2.cbz", DateAdded = DateTime.UtcNow };
            db.ChapterFiles.Add(file);
            db.SaveChanges();
            db.Chapters.Single(c => c.SeriesId == seriesId && c.Number == 2).ChapterFileId = file.Id;
            db.Series.Single(s => s.Id == seriesId).BlockedGroups = ["early birds"];
            db.SaveChanges();
        }

        await Service(source).SyncSeriesAsync(seriesId);

        var links = Links(seriesId);
        Assert.Equal("1-moon", links[1].Id);
        Assert.False(links.ContainsKey(2));
        using var check = _db.NewContext();
        Assert.Null(check.Chapters.Single(c => c.SeriesId == seriesId && c.Number == 2).DelistedAt);
    }
}
