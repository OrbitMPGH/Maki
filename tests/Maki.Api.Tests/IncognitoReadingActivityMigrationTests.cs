using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public class IncognitoReadingActivityMigrationTests
{
    [Fact]
    public async Task UpgradeKeepsLegacySittingsAndDeletingAFullSeriesKeepsItsActivityHidden()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MakiDbContext>().UseSqlite(connection).Options;
        await using var db = new MakiDbContext(options);
        await db.Database.GetInfrastructure().GetRequiredService<IMigrator>()
            .MigrateAsync("20261118153056_ResetChapterStripHashes");
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO ReadingSessions (UserId, StartedAt, EndedAt, ActiveSeconds, ChaptersCompleted)
            VALUES (1, '2026-08-01 12:00:00', '2026-08-01 12:01:00', 60, 1)
            """);
        await db.Database.MigrateAsync();

        var at = new DateTime(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);
        var root = new RootFolder { Path = "incognito-migration-fixture" };
        var series = new Series { RootFolder = root, Title = "Private series", Incognito = IncognitoMode.Full };
        db.Series.Add(series);
        await db.SaveChangesAsync();
        await new ReadingSessionService(db).RecordAsync(1, series, 120, true, at, default);
        await new ReadingProgressService(db, new ReadingProgressGate(), NullLogger<ReadingProgressService>.Instance)
            .RecordUnnumberedReadAsync(1, series.Id, series.Title, default, at);
        db.ChangeTracker.Clear();

        await db.Series.Where(s => s.Id == series.Id).ExecuteDeleteAsync();

        var saved = await db.StatsEvents.SingleAsync(e => e.Type == StatsEventType.ChaptersRead);
        Assert.Null(saved.SeriesId);
        Assert.True(saved.HiddenAfterRemoval);
        Assert.False(await db.VisibleStatsEvents().AnyAsync(e => e.Type == StatsEventType.ChaptersRead));
        var legacy = await db.VisibleReadingSessions().SingleAsync();
        Assert.Equal(60, legacy.ActiveSeconds);
        Assert.Equal(2, await db.ReadingSessions.CountAsync());

        var readded = new Series { RootFolderId = series.RootFolderId, Title = series.Title };
        db.Series.Add(readded);
        await db.SaveChangesAsync();
        await new SeriesIdentityService(db, NullLogger<SeriesIdentityService>.Instance)
            .AdoptOrphansAsync(readded, default);
        Assert.False(await db.VisibleStatsEvents().AnyAsync(e => e.Type == StatsEventType.ChaptersRead));
        Assert.Equal(1, await db.VisibleReadingSessions().CountAsync());
    }

    [Fact]
    public async Task DeletingAFullSeriesThroughTheContextKeepsItsActivityHidden()
    {
        using var fixture = new TestDb();
        var user = fixture.SeedUser("reader");
        var hidden = fixture.SeedSeries("Hidden", configure: s => s.Incognito = IncognitoMode.Full);
        var open = fixture.SeedSeries("Open");
        await using (var db = fixture.NewContext())
        {
            foreach (var series in new[] { hidden, open })
            {
                db.StatsEvents.Add(new StatsEvent
                {
                    Type = StatsEventType.ChaptersRead, UserId = user, Timestamp = DateTime.UtcNow,
                    SeriesId = series, SeriesTitle = "S", Value = 1
                });
            }
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.NewContext())
        {
            db.Series.RemoveRange(db.Series.IgnoreQueryFilters().Where(s => s.Id == hidden || s.Id == open));
            await db.SaveChangesAsync();
        }

        await using var check = fixture.NewContext();
        var orphans = await check.StatsEvents.IgnoreQueryFilters().ToListAsync();
        Assert.All(orphans, e => Assert.Null(e.SeriesId));
        Assert.Single(await check.VisibleStatsEvents().IgnoreQueryFilters().ToListAsync());
    }
}
