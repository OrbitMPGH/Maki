using Mangarr.Api.Jobs;
using Mangarr.Core.Entities;

namespace Mangarr.Api.Tests;

/// <summary>
/// The refresh-selection predicate: only a Completed series that already holds a chapter reaching
/// its known total is skipped; everything else (ongoing, unknown total, behind, no total) refreshes.
/// A series with no enabled mapping is never a candidate.
/// </summary>
public class RefreshMonitoredSeriesJobTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private int SeedWithChapters(
        SeriesStatus status, int? totalChapters, bool enabledMapping, params decimal[] chapterNumbers)
    {
        var mapping = new SourceMapping
        {
            SourceName = "fake", SourceSeriesId = "s", Url = "u", Enabled = enabledMapping
        };

        var seriesId = _db.SeedSeries(
            configure: s => { s.Status = status; s.TotalChapters = totalChapters; },
            mappings: mapping);

        using var db = _db.NewContext();
        foreach (var n in chapterNumbers)
        {
            db.Chapters.Add(new Chapter { SeriesId = seriesId, Number = n, Language = "en" });
        }
        db.SaveChanges();
        return seriesId;
    }

    private async Task<List<int>> Refreshable()
    {
        using var db = _db.NewContext();
        return await RefreshMonitoredSeriesJob.RefreshableSeriesIdsAsync(db);
    }

    [Fact]
    public async Task Completed_and_caught_up_is_skipped()
    {
        var id = SeedWithChapters(SeriesStatus.Completed, totalChapters: 100, enabledMapping: true, 99m, 100m);

        Assert.DoesNotContain(id, await Refreshable());
    }

    [Fact]
    public async Task Completed_but_behind_is_refreshed()
    {
        var id = SeedWithChapters(SeriesStatus.Completed, totalChapters: 100, enabledMapping: true, 98m, 99m);

        Assert.Contains(id, await Refreshable());
    }

    [Fact]
    public async Task Ongoing_is_always_refreshed()
    {
        var id = SeedWithChapters(SeriesStatus.Ongoing, totalChapters: 5, enabledMapping: true, 5m, 6m);

        Assert.Contains(id, await Refreshable());
    }

    [Fact]
    public async Task Completed_with_unknown_total_is_refreshed()
    {
        var id = SeedWithChapters(SeriesStatus.Completed, totalChapters: null, enabledMapping: true, 10m);

        Assert.Contains(id, await Refreshable());
    }

    [Fact]
    public async Task No_enabled_mapping_is_never_a_candidate()
    {
        var id = SeedWithChapters(SeriesStatus.Ongoing, totalChapters: null, enabledMapping: false, 1m);

        Assert.DoesNotContain(id, await Refreshable());
    }
}
