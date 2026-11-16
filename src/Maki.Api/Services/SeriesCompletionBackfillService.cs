using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// One-time pass that gives series finished before <see cref="ReadingStatus.Completed"/> existed the
/// status a read would give them now: finished, read through (<see cref="SeriesCompletion.ReadThroughAsync"/>)
/// and still at Plan to read or Reading, with no further chapter expected (those get the series page's
/// ask once the outlook goes quiet). A status set by hand (On hold, Dropped) stays. Runs at startup
/// before Kestrel and Quartz, gated by an AppConfig marker; the scrobble tick then pushes Completed.
/// </summary>
public class SeriesCompletionBackfillService(
    MakiDbContext db, IAppSettings appSettings, ReleaseOutlookService outlooks,
    ILogger<SeriesCompletionBackfillService> logger)
{
    public const string MarkerKey = "reading.completedBackfillDone";

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        if (await db.AppConfig.AnyAsync(c => c.Key == MarkerKey, ct))
        {
            return;
        }

        var candidates = await db.ReadingStates.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.Finished && r.SeriesId != null && db.Users.Any(u => u.Id == r.UserId))
            .Join(db.Series.IgnoreQueryFilters().Where(s => s.Status == SeriesStatus.Completed),
                r => r.SeriesId, s => s.Id, (r, s) => new { r.UserId, SeriesId = s.Id })
            .Where(x => !db.UserSeriesStates.IgnoreQueryFilters().Any(u =>
                u.UserId == x.UserId && u.SeriesId == x.SeriesId &&
                u.Status != ReadingStatus.PlanToRead && u.Status != ReadingStatus.Reading))
            .Distinct()
            .ToListAsync(ct);

        var unmonitorSpecials = await LibraryListRows.UnmonitorSpecialsAsync(appSettings, ct);
        var series = await db.Series.IgnoreQueryFilters().AsNoTracking()
            .Where(s => candidates.Select(c => c.SeriesId).Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, ct);

        var completed = 0;
        foreach (var c in candidates)
        {
            if (series.TryGetValue(c.SeriesId, out var s) &&
                await SeriesCompletion.ReadThroughAsync(db, c.UserId, s, unmonitorSpecials, ct) &&
                !ReleaseOutlookService.ExpectsMore(await outlooks.ForSeriesAsync(c.SeriesId, ct)))
            {
                await db.PromoteToCompletedAsync(c.UserId, c.SeriesId, ct);
                completed++;
            }
        }

        db.AppConfig.Add(new AppConfigEntry { Key = MarkerKey, Value = DateTime.UtcNow.ToString("O") });
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Reading status backfill: {Completed} of {Candidates} finished series marked Completed",
            completed, candidates.Count);
    }
}
