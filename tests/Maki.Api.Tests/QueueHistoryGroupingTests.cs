using Maki.Api.Controllers;
using Maki.Api.Dtos;
using Maki.Api.Services;
using Maki.Core.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary><see cref="QueueHistoryRuns"/> and the grouped history endpoint built on it.</summary>
public class QueueHistoryGroupingTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 8, 20, 0, 0, DateTimeKind.Utc);
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static QueueHistoryRuns.Row Row(int id, int series, int minutesAgo,
        QueueHistoryRuns.Kind kind = QueueHistoryRuns.Kind.Download, QueueStatus status = QueueStatus.Completed) =>
        new(id, series, status, T0.AddMinutes(-minutesAgo), kind);

    [Fact]
    public void A_series_burst_is_one_run_even_with_other_series_in_between()
    {
        var runs = QueueHistoryRuns.Group([Row(1, 1, 0), Row(2, 2, 1), Row(3, 1, 2), Row(4, 1, 3)]);

        Assert.Equal([[1, 3, 4], [2]], runs);
    }

    [Fact]
    public void A_gap_longer_than_the_window_starts_a_new_run()
    {
        var runs = QueueHistoryRuns.Group([Row(1, 1, 0), Row(2, 1, 20), Row(3, 1, 40), Row(4, 1, 90)]);

        Assert.Equal([[1, 2, 3], [4]], runs);
    }

    [Fact]
    public void Status_and_kind_keep_runs_apart_and_torrent_grabs_stand_alone()
    {
        var runs = QueueHistoryRuns.Group([
            Row(1, 1, 0),
            Row(2, 1, 1, status: QueueStatus.Cancelled),
            Row(3, 1, 2, QueueHistoryRuns.Kind.Upgrade),
            Row(4, 1, 3, QueueHistoryRuns.Kind.Alone),
            Row(5, 1, 4, QueueHistoryRuns.Kind.Alone),
            Row(6, 1, 5, QueueHistoryRuns.Kind.Upgrade),
            Row(7, 1, 6),
        ]);

        Assert.Equal([[1, 7], [2], [3, 6], [4], [5]], runs);
    }

    private QueueController Controller() => new(
        new TestLocalizer(), _db.NewContext(), null!, null!, null!, null!, null!, null!, null!,
        NullLogger<QueueController>.Instance);

    private int Seed(int seriesId, int minutesAgo, QueueStatus status = QueueStatus.Completed, bool upgrade = false)
    {
        using var db = _db.NewContext();
        var chapter = new Chapter { SeriesId = seriesId, Number = 100 + minutesAgo };
        db.Chapters.Add(chapter);
        db.SaveChanges();
        var item = new DownloadQueueItem
        {
            SeriesId = seriesId,
            ChapterId = chapter.Id,
            Status = status,
            QueuedAt = T0.AddDays(-1),
            CompletedAt = T0.AddMinutes(-minutesAgo),
            UpgradeInfoJson = upgrade ? "{}" : null,
        };
        db.DownloadQueue.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    private async Task<QueueHistoryGroupsDto> Grouped(int page = 1, int pageSize = 25, string? status = null, string? search = null) =>
        Assert.IsType<QueueHistoryGroupsDto>(Assert.IsType<OkObjectResult>(
            await Controller().GroupedHistory(page, pageSize, status, search, CancellationToken.None)).Value);

    [Fact]
    public async Task Grouped_history_pages_by_entry_and_never_splits_a_run()
    {
        var ordeal = _db.SeedSeries("The Mortifying Ordeal of Being Seen");
        var brother = _db.SeedSeries("She's Likely Aiming for My Older Brother");
        var a = Seed(ordeal, 0);
        var b = Seed(ordeal, 1);
        var c = Seed(brother, 2);
        var d = Seed(ordeal, 3);
        Seed(brother, 2, QueueStatus.Queued);

        var first = await Grouped(pageSize: 1);
        var second = await Grouped(page: 2, pageSize: 1);

        Assert.Equal(2, first.Total);
        Assert.Equal(4, first.ItemTotal);
        Assert.Equal([a, b, d], Assert.Single(first.Entries).Items.Select(i => i.Id));
        Assert.Equal([c], Assert.Single(second.Entries).Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Grouped_history_filters_by_status_and_series_title()
    {
        var ordeal = _db.SeedSeries("The Mortifying Ordeal of Being Seen");
        var brother = _db.SeedSeries("She's Likely Aiming for My Older Brother");
        Seed(ordeal, 0);
        var cancelled = Seed(ordeal, 1, QueueStatus.Cancelled);
        var upgrade = Seed(brother, 2, upgrade: true);
        Seed(brother, 3);

        Assert.Equal([cancelled], (await Grouped(status: "cancelled")).Entries.SelectMany(e => e.Items).Select(i => i.Id));
        Assert.Equal([upgrade], (await Grouped(status: "upgrades")).Entries.SelectMany(e => e.Items).Select(i => i.Id));

        var search = await Grouped(search: "older BROTHER");
        Assert.Equal(2, search.ItemTotal);
        Assert.All(search.Entries.SelectMany(e => e.Items), i => Assert.Equal(brother, i.SeriesId));

        Assert.Equal(0, (await Grouped(search: "100%")).Total);
    }

    [Fact]
    public async Task Flat_history_takes_the_same_filters()
    {
        var ordeal = _db.SeedSeries("The Mortifying Ordeal of Being Seen");
        var brother = _db.SeedSeries("She's Likely Aiming for My Older Brother");
        Seed(ordeal, 0);
        var mine = Seed(brother, 1);

        var dto = Assert.IsType<QueueHistoryDto>(Assert.IsType<OkObjectResult>(
            await Controller().History(search: "brother", status: "completed", ct: CancellationToken.None)).Value);

        Assert.Equal(1, dto.Total);
        Assert.Equal(mine, Assert.Single(dto.Items).Id);
    }
}
