using Mangarr.Core.Entities;
using Mangarr.Data;
using Microsoft.EntityFrameworkCore;

namespace Mangarr.Api.Services;

/// <summary>
/// One-time seed of the Rewind activity log from data that predates it: series-added events
/// from <see cref="Series.Added"/> and download events from <see cref="ChapterFile.DateAdded"/>
/// (the durable download record — queue history is purged after 30 days). Reading history has
/// no backfillable source; the first scrobble sync creates silent baselines instead.
/// Runs at startup before Kestrel and Quartz, gated by an AppConfig marker, so it can never
/// overlap or duplicate live event hooks.
/// </summary>
public class StatsBackfillService(MangarrDbContext db, ILogger<StatsBackfillService> logger)
{
    public const string MarkerKey = "stats.backfillDone";

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        if (await db.AppConfig.AnyAsync(c => c.Key == MarkerKey, ct))
        {
            return;
        }

        var seriesRows = await db.Series.AsNoTracking()
            .Select(s => new { s.Id, s.Title, s.Added })
            .ToListAsync(ct);
        foreach (var s in seriesRows)
        {
            db.StatsEvents.Add(new StatsEvent
            {
                Type = StatsEventType.SeriesAdded,
                Timestamp = s.Added,
                SeriesId = s.Id,
                SeriesTitle = s.Title
            });
        }

        // One row per (series, UTC day) keeps day precision while compressing bulk imports.
        var titles = seriesRows.ToDictionary(s => s.Id, s => s.Title);
        var fileRows = await db.ChapterFiles.AsNoTracking()
            .Select(f => new { f.SeriesId, f.DateAdded })
            .ToListAsync(ct);
        foreach (var group in fileRows.GroupBy(f => new { f.SeriesId, f.DateAdded.Date }))
        {
            db.StatsEvents.Add(new StatsEvent
            {
                Type = StatsEventType.ChapterDownloaded,
                Timestamp = group.Key.Date,
                SeriesId = group.Key.SeriesId,
                SeriesTitle = titles.GetValueOrDefault(group.Key.SeriesId, ""),
                Value = group.Count()
            });
        }

        db.AppConfig.Add(new AppConfigEntry
        {
            Key = MarkerKey,
            Value = DateTime.UtcNow.ToString("O")
        });
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Stats backfill complete: {Series} series-added event(s) and {Files} chapter file(s) seeded as daily download events",
            seriesRows.Count, fileRows.Count);
    }
}
