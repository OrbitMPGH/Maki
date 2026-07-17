using Mangarr.Core.Entities;
using Mangarr.Core.Http;
using Mangarr.Core.Sources;
using Mangarr.Data;
using Microsoft.EntityFrameworkCore;

namespace Mangarr.Api.Services;

/// <summary>
/// Refreshes a series' chapter list from its source mappings and diffs it
/// against what is already known. New chapters are inserted; existing ones
/// are matched by (Number, Language) — or title for one-shots. Volume is a
/// wildcard: sources disagree on whether chapters carry volume info, so a
/// null volume on either side still matches, and volume-aware sources
/// backfill the volume onto chapters first seen without one.
/// </summary>
public class ChapterSyncService(
    MangarrDbContext db,
    SourceRegistry sourceRegistry,
    DownloadQueueService queue,
    ILogger<ChapterSyncService> logger)
{
    /// <returns>Ids of newly discovered chapters.</returns>
    public async Task<List<int>> SyncSeriesAsync(int seriesId, CancellationToken ct = default)
    {
        var series = await db.Series
            .Include(s => s.SourceMappings)
            .FirstOrDefaultAsync(s => s.Id == seriesId, ct)
            ?? throw new InvalidOperationException($"Series {seriesId} not found");

        var existing = await db.Chapters.Where(c => c.SeriesId == seriesId).ToListAsync(ct);
        MergeDuplicates(existing);
        var newChapters = new List<Chapter>();
        var numbersBySource = new Dictionary<string, IReadOnlyCollection<decimal?>>();

        // MangaBaka has no MangaDex ids, so the uuid can only come from a linked
        // source mapping; it feeds the series web links.
        series.MangaDexUuid ??= series.SourceMappings
            .FirstOrDefault(m => m.SourceName == "mangadex")?.SourceSeriesId;

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
                numbersBySource[mapping.SourceName] = sourceChapters.Select(sc => sc.Number).ToList();

                foreach (var sc in sourceChapters)
                {
                    var match = FindMatch(existing, sc);
                    if (match is not null)
                    {
                        // Enrich rather than duplicate: a volume-aware source fills in
                        // what a volume-less source couldn't provide.
                        match.Volume ??= sc.Volume;
                        match.Title ??= sc.Title;
                        match.ReleaseDate ??= sc.ReleaseDate;
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
                        Monitored = Chapter.MonitoredUnder(series.MonitorNewItems, sc.Number)
                    };
                    db.Chapters.Add(chapter);
                    existing.Add(chapter);
                    newChapters.Add(chapter);
                }

                mapping.LastRefresh = DateTime.UtcNow;
                mapping.LastError = null;
            }
            catch (Exception ex) when (RateLimitDetector.IsRateLimit(ex, out var retryAfter))
            {
                // The source is throttling us, and it doesn't care which subsystem is asking —
                // so back the download queue off too rather than letting it walk into the same
                // 429s seconds later. The sync itself still just records the error and moves on.
                var until = queue.EnterRateLimitCooldown(retryAfter);
                logger.LogWarning(
                    "Rate limited by {Source} during chapter sync of series {SeriesId}; " +
                    "pausing scraper downloads until {Until:o}",
                    mapping.SourceName, seriesId, until);
                mapping.LastError = $"Rate limited — downloads paused until {until.ToLocalTime():HH:mm:ss}";
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Chapter sync failed for {Source} mapping of series {SeriesId}",
                    mapping.SourceName, seriesId);
                mapping.LastError = ex.Message;
            }
        }

        // Flag (or clear) cross-source numbering clashes. A clash is always
        // recorded; clearing requires every enabled mapping to have fetched this
        // run, so one temporarily failing source doesn't wipe a real flag.
        var clash = NumberingClashDetector.Detect(numbersBySource);
        var value = clash is null ? null : $"{clash.SubChapterSource}|{clash.WholeChapterSource}";
        var allFetched = numbersBySource.Count == series.SourceMappings.Count(m => m.Enabled);
        if (value is not null || allFetched)
        {
            if (value != series.NumberingClash)
            {
                logger.LogInformation("Numbering clash on series {SeriesId}: {Value}", seriesId, value ?? "cleared");
            }

            series.NumberingClash = value;
        }

        await db.SaveChangesAsync(ct);
        return newChapters.Select(c => c.Id).ToList();
    }

    private static Chapter? FindMatch(List<Chapter> existing, SourceChapter sc)
    {
        if (sc.Number is not null)
        {
            return existing.FirstOrDefault(c =>
                c.Number == sc.Number &&
                c.Language == sc.Language &&
                (c.Volume is null || sc.Volume is null || c.Volume == sc.Volume));
        }

        return existing.FirstOrDefault(c =>
            c.IsOneShot &&
            c.Language == sc.Language &&
            string.Equals(c.Title, sc.Title, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Heals duplicates created before volume became a wildcard in matching:
    /// the same chapter synced once with a volume ("Vol.4 Ch.27") and once
    /// without ("Ch.27"). Keeps the richest copy and deletes the rest.
    /// </summary>
    private void MergeDuplicates(List<Chapter> existing)
    {
        var groups = existing
            .Where(c => c.Number is not null)
            .GroupBy(c => (c.Number, c.Language))
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in groups)
        {
            // Two different explicit volumes means per-volume numbering, not a duplicate.
            if (group.Select(c => c.Volume).OfType<int>().Distinct().Count() > 1)
            {
                continue;
            }

            var keeper = group
                .OrderByDescending(c => c.ChapterFileId != null)
                .ThenByDescending(c => c.Volume != null)
                .ThenBy(c => c.Id)
                .First();

            foreach (var dup in group.Where(c => !ReferenceEquals(c, keeper)))
            {
                keeper.Volume ??= dup.Volume;
                keeper.Title ??= dup.Title;
                keeper.ReleaseDate ??= dup.ReleaseDate;
                keeper.ChapterFileId ??= dup.ChapterFileId;
                keeper.Monitored |= dup.Monitored;
                existing.Remove(dup);
                db.Chapters.Remove(dup);
            }

            logger.LogInformation("Merged {Count} duplicate row(s) of chapter {Number} in series {SeriesId}",
                group.Count() - 1, keeper.Number, keeper.SeriesId);
        }
    }
}
