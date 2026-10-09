using Maki.Api.Jobs;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Maki.Api.Tests;

public class FailedQueueRetentionTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly DateTime _now = DateTime.UtcNow;

    public void Dispose() => _db.Dispose();

    private void Seed(string name, DateTime queuedAt, DateTime? nextAttempt = null)
    {
        var seriesId = _db.SeedSeries(name);
        using var db = _db.NewContext();
        var chapter = new Chapter { SeriesId = seriesId, Number = 1m, Language = "en" };
        db.Chapters.Add(chapter);
        db.SaveChanges();
        db.DownloadQueue.Add(new DownloadQueueItem
        {
            SeriesId = seriesId, ChapterId = chapter.Id, Status = QueueStatus.Failed,
            QueuedAt = queuedAt, NextAttempt = nextAttempt, Title = name,
        });
        db.SaveChanges();
    }

    private (List<string?> Stale, List<string?> Recent) Classify()
    {
        using var db = _db.NewContext();
        var cutoff = _now.AddDays(-30);
        return (
            HousekeepingJob.StaleFailures(db.DownloadQueue, _now, cutoff).Select(q => q.Title).ToList(),
            HousekeepingJob.RecentFailures(db.DownloadQueue, _now, cutoff).Select(q => q.Title).ToList());
    }

    [Fact]
    public void Only_old_failures_that_are_not_waiting_on_a_retry_are_stale()
    {
        Seed("old", _now.AddDays(-40));
        Seed("recent", _now.AddDays(-1));
        Seed("parked", _now.AddDays(-40), nextAttempt: _now.AddDays(2));
        Seed("retry-date-passed-recently", _now.AddDays(-40), nextAttempt: _now.AddDays(-1));
        Seed("retry-date-passed-long-ago", _now.AddDays(-60), nextAttempt: _now.AddDays(-45));

        var (stale, recent) = Classify();

        Assert.Equal(["old", "retry-date-passed-long-ago"], stale.Order().ToList());
        Assert.Equal(["recent", "retry-date-passed-recently"], recent.Order().ToList());
    }
}

public class ConcurrentFirstSettingWriteTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    /// <summary>Inserts the same row from another context just before the first save lands.</summary>
    private sealed class RacingInsert(Action insert) : SaveChangesInterceptor
    {
        private int _fired;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _fired, 1) == 0) insert();
            return ValueTask.FromResult(result);
        }
    }

    private MakiDbContext RacingContext(Action insert) =>
        new(new DbContextOptionsBuilder<MakiDbContext>(_db.Options).AddInterceptors(new RacingInsert(insert)).Options);

    [Fact]
    public async Task Instance_setting_survives_a_concurrent_first_write()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => RacingContext(() => _db.SetConfig(("race.key", "theirs"))));
        var settings = new SettingsService(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());

        await settings.SetAsync("race.key", "mine");

        using var db = _db.NewContext();
        Assert.Equal("mine", db.AppConfig.Single(c => c.Key == "race.key").Value);
    }

    [Fact]
    public async Task User_setting_survives_a_concurrent_first_write()
    {
        var user = _db.SeedUser("racer", Maki.Core.Security.MakiPermission.None);
        using var db = RacingContext(() => _db.SetUserConfig(user, ("race.key", "theirs")));

        await UserSettingsStore.SetAsync(db, user, "race.key", "mine");

        using var check = _db.NewContext();
        Assert.Equal("mine", check.UserSettings.Single(s => s.UserId == user && s.Key == "race.key").Value);
    }
}
