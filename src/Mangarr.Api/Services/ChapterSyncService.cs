using Mangarr.Core.Entities;
using Mangarr.Core.Sources;
using Mangarr.Data;
using Microsoft.EntityFrameworkCore;

namespace Mangarr.Api.Services;

/// <summary>
/// Refreshes a series' chapter list from its source mappings and diffs it
/// against what is already known. New chapters are inserted; existing ones
/// are matched by (Number, Volume, Language) — or title for one-shots.
/// </summary>
public class ChapterSyncService(
    MangarrDbContext db,
    SourceRegistry sourceRegistry,
    ILogger<ChapterSyncService> logger)
{
    /// <returns>Number of newly discovered chapters.</returns>
    public async Task<int> SyncSeriesAsync(int seriesId, CancellationToken ct = default)
    {
        var series = await db.Series
            .Include(s => s.SourceMappings)
            .FirstOrDefaultAsync(s => s.Id == seriesId, ct)
            ?? throw new InvalidOperationException($"Series {seriesId} not found");

        var existing = await db.Chapters.Where(c => c.SeriesId == seriesId).ToListAsync(ct);
        var newChapters = 0;

        foreach (var mapping in series.SourceMappings.Where(m => m.Enabled))
        {
            var source = sourceRegistry.Find(mapping.SourceName);
            if (source is null)
            {
                logger.LogWarning("Mapping {Id} references unknown source {Source}", mapping.Id, mapping.SourceName);
                continue;
            }

            try
            {
                var sourceChapters = await source.ListChaptersAsync(mapping.SourceSeriesId, mapping.LanguageFilter, ct);

                foreach (var sc in sourceChapters)
                {
                    var match = FindMatch(existing, sc);
                    if (match is not null)
                    {
                        continue;
                    }

                    var chapter = new Chapter
                    {
                        SeriesId = seriesId,
                        Number = sc.Number,
                        NumberRaw = sc.NumberRaw,
                        Volume = sc.Volume,
                        Title = sc.Title,
                        IsOneShot = sc.Number is null,
                        Language = sc.Language,
                        ReleaseDate = sc.ReleaseDate,
                        Monitored = series.Monitored && series.MonitorNewItems == NewChapterMonitorMode.All
                    };
                    db.Chapters.Add(chapter);
                    existing.Add(chapter);
                    newChapters++;
                }

                mapping.LastRefresh = DateTime.UtcNow;
                mapping.LastError = null;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Chapter sync failed for {Source} mapping of series {SeriesId}",
                    mapping.SourceName, seriesId);
                mapping.LastError = ex.Message;
            }
        }

        await db.SaveChangesAsync(ct);
        return newChapters;
    }

    private static Chapter? FindMatch(List<Chapter> existing, SourceChapter sc)
    {
        if (sc.Number is not null)
        {
            return existing.FirstOrDefault(c =>
                c.Number == sc.Number &&
                c.Volume == sc.Volume &&
                c.Language == sc.Language);
        }

        return existing.FirstOrDefault(c =>
            c.IsOneShot &&
            c.Language == sc.Language &&
            string.Equals(c.Title, sc.Title, StringComparison.OrdinalIgnoreCase));
    }
}
