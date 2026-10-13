using Maki.Api.Jobs;
using Maki.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Tests;

public class HealthScanRetentionTests
{
    [Fact]
    public async Task Old_finished_scans_go_but_the_ten_newest_and_live_ones_stay()
    {
        using var fixture = new TestDb();
        var now = new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);
        var old = now.AddDays(-60);

        using (var db = fixture.NewContext())
        {
            db.HealthScans.Add(new HealthScan { Status = "pending", CreatedAt = old });
            db.HealthScans.Add(new HealthScan { Status = "running", CreatedAt = old });
            for (var i = 0; i < 20; i++)
            {
                db.HealthScans.Add(new HealthScan { Status = "completed", CreatedAt = old });
            }

            db.HealthScans.Add(new HealthScan { Status = "completed", CreatedAt = now.AddDays(-1) });
            await db.SaveChangesAsync();
        }

        using var prune = fixture.NewContext();
        var removed = await HousekeepingJob.PruneHealthScansAsync(prune, now.AddDays(-30), default);

        var remaining = await prune.HealthScans.OrderBy(s => s.Id).ToListAsync();
        Assert.Equal(11, removed);
        Assert.Equal(12, remaining.Count);
        Assert.Contains(remaining, s => s.Id == 1);
        Assert.Contains(remaining, s => s.Id == 2);
        Assert.Equal(23, remaining[^1].Id);
    }

    [Fact]
    public async Task Nothing_is_removed_while_there_are_fewer_than_ten_scans()
    {
        using var fixture = new TestDb();
        using var db = fixture.NewContext();
        for (var i = 0; i < 5; i++)
        {
            db.HealthScans.Add(new HealthScan { Status = "completed", CreatedAt = DateTime.UtcNow.AddDays(-90) });
        }

        await db.SaveChangesAsync();

        Assert.Equal(0, await HousekeepingJob.PruneHealthScansAsync(db, DateTime.UtcNow.AddDays(-30), default));
    }
}
