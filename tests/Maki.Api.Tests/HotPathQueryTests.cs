using Maki.Api.Controllers;
using Maki.Api.Dtos;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// Query shapes on request paths that run constantly: the queue badge's counts, the queue pages'
/// projections, and the explicit user term that lets SQLite seek a UserId-leading index.
/// </summary>
public sealed class HotPathQueryTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static QueueController Queue(MakiDbContext db) => new(
        localizer: new TestLocalizer(),
        db: db,
        queue: null!,
        batches: null!,
        importer: null!,
        events: null!,
        schedulerFactory: null!,
        logger: NullLogger<QueueController>.Instance);

    private int SeedQueue(int seriesId, QueueStatus status, DateTime? completedAt = null, string? sourceName = null)
    {
        using var db = _db.NewContext();
        SourceMapping? mapping = null;
        if (sourceName is not null)
        {
            mapping = db.SourceMappings.FirstOrDefault(m => m.SeriesId == seriesId && m.SourceName == sourceName)
                      ?? new SourceMapping { SeriesId = seriesId, SourceName = sourceName, SourceSeriesId = "x" };
        }

        var chapter = new Chapter { SeriesId = seriesId, Number = 12.5m, Volume = 3 };
        db.Chapters.Add(chapter);
        db.SaveChanges();

        var item = new DownloadQueueItem
        {
            SeriesId = seriesId,
            ChapterId = chapter.Id,
            SourceMapping = mapping,
            Status = status,
            QueuedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CompletedAt = completedAt,
        };
        db.DownloadQueue.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    [Fact]
    public async Task Queue_summary_counts_the_whole_queue_by_what_the_badge_shows()
    {
        var seriesId = _db.SeedSeries();
        foreach (var status in new[]
                 {
                     QueueStatus.Queued, QueueStatus.Downloading, QueueStatus.RateLimited, QueueStatus.Resolving,
                     QueueStatus.AwaitingImport, QueueStatus.Failed, QueueStatus.Failed,
                     QueueStatus.Completed, QueueStatus.Cancelled,
                 })
        {
            SeedQueue(seriesId, status);
        }

        using var db = _db.NewContext(userId: 1);
        var summary = Assert.IsType<QueueSummaryDto>(
            Assert.IsType<OkObjectResult>(await Queue(db).Summary(CancellationToken.None)).Value);

        Assert.Equal(new QueueSummaryDto(Active: 4, AwaitingImport: 1, Failed: 2), summary);
    }

    [Fact]
    public async Task Queue_pages_project_what_the_rows_show()
    {
        var seriesId = _db.SeedSeries("Berserk");
        SeedQueue(seriesId, QueueStatus.Downloading, sourceName: "mangadex");
        var at = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var older = SeedQueue(seriesId, QueueStatus.Completed, completedAt: at);
        var newer = SeedQueue(seriesId, QueueStatus.Completed, completedAt: at);

        using var db = _db.NewContext(userId: 1);
        var active = Assert.IsType<QueueHistoryDto>(
            Assert.IsType<OkObjectResult>(await Queue(db).List(ct: CancellationToken.None)).Value);
        var item = Assert.Single(active.Items);
        Assert.Equal("Berserk", item.SeriesTitle);
        Assert.Equal("mangadex", item.SourceName);
        Assert.Equal("12.5", item.ChapterNumber);
        Assert.Equal(3, item.ChapterVolume);

        // Same timestamp on both: the id decides, newest first, so paging cannot repeat or skip one.
        var history = Assert.IsType<QueueHistoryDto>(
            Assert.IsType<OkObjectResult>(await Queue(db).History(ct: CancellationToken.None)).Value);
        Assert.Equal([newer, older], history.Items.Select(i => i.Id));
        Assert.All(history.Items, i => Assert.Equal("?", i.SourceName));
    }

    [Fact]
    public void The_home_rail_seeks_the_user_index()
    {
        using var db = _db.NewContext(userId: 1);
        var query = db.ChapterProgress
            .AsNoTracking()
            .OwnedByScopeUser(db)
            .Where(p => !p.Watched)
            .OrderByDescending(p => p.UpdatedAt)
            .Take(2000)
            .Select(p => new { p.SeriesId, p.Completed, p.UnreadAt, p.PageIndex, p.UpdatedAt });

        var plan = Plan(db, query.ToQueryString());

        Assert.Contains(plan, p => p.Contains("IX_ChapterProgress_UserId_UpdatedAt (UserId=?)", StringComparison.Ordinal));
    }

    [Fact]
    public void The_inbox_badge_seeks_the_user_index()
    {
        using var db = _db.NewContext(userId: 1);
        var query = db.UserNotifications.OwnedByScopeUser(db).Where(n => n.ReadAt == null);

        var plan = Plan(db, query.ToQueryString());

        Assert.Contains(plan, p => p.Contains("IX_UserNotifications_UserId_ReadAt (UserId=? AND ReadAt=?)", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unrestricted_scope_adds_no_user_term()
    {
        using var db = _db.NewContext();

        Assert.Equal(db.ChapterProgress.ToQueryString(), db.ChapterProgress.OwnedByScopeUser(db).ToQueryString());
    }

    private static List<string> Plan(MakiDbContext db, string queryString)
    {
        using var command = db.Database.GetDbConnection().CreateCommand();
        var sql = new List<string>();
        foreach (var line in queryString.Split('\n'))
        {
            // ToQueryString renders parameters as sqlite3 shell ".param set @name value" lines.
            if (line.StartsWith(".param set ", StringComparison.Ordinal))
            {
                var parts = line.TrimEnd('\r').Split(' ', 4);
                command.Parameters.Add(new SqliteParameter(parts[2], parts[3].Trim('\'')));
            }
            else
            {
                sql.Add(line);
            }
        }

        command.CommandText = "EXPLAIN QUERY PLAN " + string.Join('\n', sql);
        var plan = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            plan.Add(reader.GetString(3));
        }

        return plan;
    }
}
