using Maki.Api.Services;
using Maki.Core.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>The one-time seed of sittings from historical reading-time events.</summary>
public sealed class ReadingSessionBackfillTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 3, 1, 20, 0, 0, DateTimeKind.Utc);

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private async Task Run()
    {
        await using var db = _db.NewContext();
        await new ReadingSessionBackfillService(db, NullLogger<ReadingSessionBackfillService>.Instance)
            .RunOnceAsync();
    }

    private void Time(int userId, DateTime at, int seconds)
    {
        using var db = _db.NewContext();
        db.StatsEvents.Add(new StatsEvent
        {
            Type = StatsEventType.ReadingTime,
            UserId = userId,
            Timestamp = at,
            SeriesTitle = "S",
            Value = seconds
        });
        db.SaveChanges();
    }

    private List<ReadingSession> Sessions(int userId)
    {
        using var db = _db.NewContext();
        return db.ReadingSessions.Where(s => s.UserId == userId).OrderBy(s => s.StartedAt).ToList();
    }

    [Fact]
    public async Task StitchesEachUsersEventsWithTheGapRule()
    {
        var a = _db.SeedUser("a");
        var b = _db.SeedUser("b");

        // a: two events 5 minutes apart (one sitting), then one an hour later (a second).
        Time(a, T0, 300);
        Time(a, T0.AddMinutes(5), 300);
        Time(a, T0.AddHours(1), 120);
        // b: interleaved in time with a, but must never join a's sittings.
        Time(b, T0.AddMinutes(2), 60);

        await Run();

        var sa = Sessions(a);
        Assert.Equal(2, sa.Count);
        Assert.Equal(T0.AddSeconds(-300), sa[0].StartedAt);
        Assert.Equal(T0.AddMinutes(5), sa[0].EndedAt);
        Assert.Equal(600, sa[0].ActiveSeconds);
        Assert.Equal(120, sa[1].ActiveSeconds);

        var sb = Assert.Single(Sessions(b));
        Assert.Equal(60, sb.ActiveSeconds);
    }

    [Fact]
    public async Task RunsOnceBehindItsMarker()
    {
        var a = _db.SeedUser("a");
        Time(a, T0, 300);

        await Run();
        await Run();

        Assert.Single(Sessions(a));
        using var db = _db.NewContext();
        Assert.Single(db.AppConfig, c => c.Key == ReadingSessionBackfillService.MarkerKey);
    }

    [Fact]
    public async Task SkipsUsersThatAlreadyHaveSessions()
    {
        var a = _db.SeedUser("a");
        var b = _db.SeedUser("b");
        using (var db = _db.NewContext())
        {
            db.ReadingSessions.Add(new ReadingSession
            {
                UserId = a, StartedAt = T0.AddDays(1), EndedAt = T0.AddDays(1).AddMinutes(5), ActiveSeconds = 300
            });
            db.SaveChanges();
        }

        Time(a, T0, 300);
        Time(b, T0, 300);

        await Run();

        Assert.Equal(T0.AddDays(1), Assert.Single(Sessions(a)).StartedAt);
        Assert.Single(Sessions(b));
    }
}
