using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// One-time seed of <see cref="ReadingSession"/> rows from the <see cref="StatsEventType.ReadingTime"/>
/// events that predate the table. Each event covers <c>(Timestamp - Value, Timestamp]</c> and is
/// stitched with the same gap rule as live reports (<see cref="ReadingSessionService.Stitch"/>).
/// Chapter completions are not reconstructed, so backfilled sittings carry zero.
/// Runs at startup before Kestrel and Quartz, gated by an AppConfig marker, so it can never
/// overlap live stitching.
/// </summary>
public class ReadingSessionBackfillService(MakiDbContext db, ILogger<ReadingSessionBackfillService> logger)
{
    public const string MarkerKey = "stats.sessionsBackfillDone";

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        if (await db.AppConfig.AnyAsync(c => c.Key == MarkerKey, ct))
        {
            return;
        }

        var userIds = await db.StatsEvents.IgnoreQueryFilters()
            .Where(e => e.Type == StatsEventType.ReadingTime && e.UserId != null)
            // A row for a user that no longer exists would fail the session's foreign key.
            .Where(e => db.Users.Any(u => u.Id == e.UserId))
            .Select(e => e.UserId!.Value)
            .Distinct()
            .ToListAsync(ct);

        var seeded = 0;
        foreach (var userId in userIds)
        {
            if (await db.ReadingSessions.IgnoreQueryFilters().AnyAsync(s => s.UserId == userId, ct))
            {
                continue;
            }

            var events = await db.StatsEvents.IgnoreQueryFilters().AsNoTracking()
                .Where(e => e.Type == StatsEventType.ReadingTime && e.UserId == userId && e.Value > 0)
                .OrderBy(e => e.Timestamp)
                .Select(e => new { e.Timestamp, e.Value })
                .ToListAsync(ct);

            var sessions = new List<ReadingSession>();
            ReadingSession? latest = null;
            foreach (var e in events)
            {
                var landed = ReadingSessionService.Stitch(latest, userId, e.Value, false, e.Timestamp);
                if (landed is not null && !ReferenceEquals(landed, latest))
                {
                    sessions.Add(landed);
                    latest = landed;
                }
            }

            db.ReadingSessions.AddRange(sessions);
            await db.SaveChangesAsync(ct);
            seeded += sessions.Count;
        }

        db.AppConfig.Add(new AppConfigEntry
        {
            Key = MarkerKey,
            Value = DateTime.UtcNow.ToString("O")
        });
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Reading session backfill complete: {Sessions} session(s) seeded for {Users} user(s)",
            seeded, userIds.Count);
    }
}
