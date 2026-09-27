using System.Data.Common;
using Maki.Api.Hubs;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// One active queue row per chapter, enforced by the unique index on the computed
/// <see cref="DownloadQueueItem.ActiveChapterId"/> rather than by a check-then-insert.
/// </summary>
public class DownloadQueueActiveChapterTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private (int SeriesId, int ChapterId) SeedChapter()
    {
        var seriesId = _db.SeedSeries(mappings:
            [new SourceMapping { SourceName = "fake", SourceSeriesId = "s", Url = "https://fake.test", Priority = 1, Enabled = true }]);
        using var db = _db.NewContext();
        var chapter = new Chapter { SeriesId = seriesId, Number = 1m, Language = "en" };
        db.Chapters.Add(chapter);
        db.SaveChanges();
        return (seriesId, chapter.Id);
    }

    private int SeedRow(int seriesId, int chapterId, QueueStatus status)
    {
        using var db = _db.NewContext();
        var item = new DownloadQueueItem
        {
            SeriesId = seriesId, ChapterId = chapterId, Protocol = AcquisitionProtocol.Scraper,
            Status = status, QueuedAt = T0.UtcDateTime
        };
        db.DownloadQueue.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    private DownloadQueueService Queue(IServiceScopeFactory? scopes = null) => new(
        scopes ?? _db.ScopeFactory(), new StoppedClock(T0), Sources.SingleChapterResolver(null, "fake"),
        NullLogger<DownloadQueueService>.Instance);

    [Fact]
    public void A_second_active_row_for_a_chapter_is_rejected_by_the_database()
    {
        var (seriesId, chapterId) = SeedChapter();
        SeedRow(seriesId, chapterId, QueueStatus.Queued);

        var ex = Assert.Throws<DbUpdateException>(() => SeedRow(seriesId, chapterId, QueueStatus.FetchingPages));
        Assert.Equal(2067, Assert.IsType<SqliteException>(ex.InnerException).SqliteExtendedErrorCode);
    }

    [Fact]
    public void Settled_rows_do_not_count_against_the_chapter()
    {
        var (seriesId, chapterId) = SeedChapter();
        SeedRow(seriesId, chapterId, QueueStatus.Completed);
        SeedRow(seriesId, chapterId, QueueStatus.Failed);
        SeedRow(seriesId, chapterId, QueueStatus.Cancelled);
        var active = SeedRow(seriesId, chapterId, QueueStatus.Queued);

        using var db = _db.NewContext();
        Assert.Equal(active, db.DownloadQueue.Single(q => q.ActiveChapterId == chapterId).Id);
    }

    [Theory]
    [InlineData(QueueStatus.Completed)]
    [InlineData(QueueStatus.Failed)]
    [InlineData(QueueStatus.Cancelled)]
    public async Task Settling_a_row_frees_the_chapter_to_be_queued_again(QueueStatus settled)
    {
        var (seriesId, chapterId) = SeedChapter();
        var id = SeedRow(seriesId, chapterId, QueueStatus.Downloading);
        var queue = Queue();

        Assert.Null(await queue.EnqueueChapterAsync(chapterId));

        using (var db = _db.NewContext())
        {
            var row = db.DownloadQueue.Single(q => q.Id == id);
            Assert.Equal(chapterId, row.ActiveChapterId);
            row.Status = settled;
            db.SaveChanges();
            Assert.Null(row.ActiveChapterId);
        }

        var again = await queue.EnqueueChapterAsync(chapterId);
        Assert.NotNull(again);
        Assert.NotEqual(id, again!.Id);
    }

    [Fact]
    public async Task Losing_the_insert_race_reads_as_already_queued()
    {
        var (seriesId, chapterId) = SeedChapter();
        var competitor = new CompetingInsert(seriesId, chapterId);
        var queue = Queue(ScopeFactory(competitor));

        var result = await queue.EnqueueChapterAsync(chapterId);

        Assert.True(competitor.Fired);
        Assert.Null(result);
        using var db = _db.NewContext();
        var row = Assert.Single(db.DownloadQueue.Where(q => q.ChapterId == chapterId));
        Assert.Equal(QueueStatus.Queued, row.Status);
    }

    [Fact]
    public async Task Losing_the_insert_race_with_a_pin_returns_the_existing_row()
    {
        var (seriesId, chapterId) = SeedChapter();
        int mappingId;
        using (var db = _db.NewContext())
        {
            mappingId = db.SourceMappings.Single(m => m.SeriesId == seriesId).Id;
        }
        var competitor = new CompetingInsert(seriesId, chapterId);
        var queue = Queue(ScopeFactory(competitor));

        var result = await queue.EnqueueChapterAsync(chapterId, preferMappingId: mappingId);

        Assert.True(competitor.Fired);
        Assert.NotNull(result);
        using var check = _db.NewContext();
        Assert.Equal(result!.Id, Assert.Single(check.DownloadQueue.Where(q => q.ChapterId == chapterId)).Id);
    }

    [Fact]
    public async Task Requeueing_failures_skips_a_chapter_that_is_queued_again_and_takes_one_row_per_chapter()
    {
        var (seriesId, chapterId) = SeedChapter();
        SeedRow(seriesId, chapterId, QueueStatus.Failed);
        SeedRow(seriesId, chapterId, QueueStatus.Failed);
        var active = SeedRow(seriesId, chapterId, QueueStatus.Queued);
        var queue = Queue();

        Assert.Equal(0, await queue.RequeueEligibleFailuresAsync(5));

        using (var db = _db.NewContext())
        {
            db.DownloadQueue.Single(q => q.Id == active).Status = QueueStatus.Completed;
            db.SaveChanges();
        }

        Assert.Equal(1, await queue.RequeueEligibleFailuresAsync(5));
        using var check = _db.NewContext();
        Assert.Single(check.DownloadQueue.Where(q => q.ActiveChapterId == chapterId));
    }

    [Fact]
    public void Migration_applies_to_a_fresh_database_and_cancels_duplicate_active_rows()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<MakiDbContext>().UseSqlite(connection).Options;
        using (var before = new MakiDbContext(options))
        {
            before.Database.GetInfrastructure().GetRequiredService<IMigrator>()
                .Migrate("20260926191801_DownloadQueueClaimIndex");
            before.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");
            foreach (var status in new[] { QueueStatus.Queued, QueueStatus.Downloading, QueueStatus.Completed })
            {
                before.Database.ExecuteSqlRaw(
                    """
                    INSERT INTO "DownloadQueue" ("SeriesId", "ChapterId", "Protocol", "Status", "PagesTotal",
                        "PagesDone", "RetryCount", "QueuedAt", "SortOrder", "Origin")
                    VALUES (1, 7, 0, {0}, 0, 0, 0, '2026-01-01 00:00:00', 0, 0)
                    """, (int)status);
            }
        }

        using var after = new MakiDbContext(options);
        after.Database.Migrate();
        var rows = after.DownloadQueue.IgnoreQueryFilters().AsNoTracking().OrderBy(q => q.Id).ToList();
        Assert.Equal([QueueStatus.Queued, QueueStatus.Cancelled, QueueStatus.Completed], rows.Select(r => r.Status));
        Assert.Equal([7, null, null], rows.Select(r => r.ActiveChapterId));
    }

    private IServiceScopeFactory ScopeFactory(IInterceptor interceptor)
    {
        var options = new DbContextOptionsBuilder<MakiDbContext>(_db.Options).AddInterceptors(interceptor).Options;
        var services = new ServiceCollection();
        services.AddScoped(_ => new MakiDbContext(options));
        services.AddSingleton<EventBroadcaster>(
            sp => new EventBroadcaster(new NoopHubContext(), sp.GetRequiredService<IServiceScopeFactory>()));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    /// <summary>Lands another enqueue's row just before this one's insert, after its duplicate check.</summary>
    private sealed class CompetingInsert(int seriesId, int chapterId) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (!Fired && command.CommandText.Contains("INSERT INTO \"DownloadQueue\""))
            {
                Fired = true;
                await using var other = command.Connection!.CreateCommand();
                other.Transaction = command.Transaction;
                other.CommandText =
                    $"""
                     INSERT INTO "DownloadQueue" ("SeriesId", "ChapterId", "Protocol", "Status", "PagesTotal",
                         "PagesDone", "RetryCount", "QueuedAt", "SortOrder", "Origin")
                     VALUES ({seriesId}, {chapterId}, 0, {(int)QueueStatus.Queued}, 0, 0, 0, '2026-01-01 00:00:00', 0, 0)
                     """;
                await other.ExecuteNonQueryAsync(cancellationToken);
            }

            return result;
        }
    }
}
