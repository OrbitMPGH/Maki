using Maki.Api.Dtos;
using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Maki.Api.Services;

/// <summary>
/// Composition of the library itself: how big it is, what it is made of, where it came from.
/// <para>
/// Every query here runs with the global filters left on, so it answers about the root folders the
/// caller can see and nothing else. That is also why the cache is keyed by user — two people with
/// different root folders must not share an entry.
/// </para>
/// </summary>
public class LibraryCompositionService(MakiDbContext db, ICurrentUser currentUser, IMemoryCache cache)
{
    /// <summary>
    /// Short, like <see cref="UserMetricsService"/>'s. Heavier than Rewind and re-requested on every
    /// tab switch, while nothing here changes faster than a download completing.
    /// </summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    private const int TopGenreCount = 15;
    private const int LargestSeriesCount = 10;

    // Matches HousekeepingJob's DownloadQueue retention for Completed/Cancelled rows (Failed rows
    // are kept indefinitely), so reliability and monitor-catch counts never undercount completions
    // relative to failures older rows would still show.
    private static readonly TimeSpan ReliabilityWindow = TimeSpan.FromDays(30);
    private static readonly TimeSpan RequestResolvedWindow = TimeSpan.FromDays(90);

    public async Task<LibraryCompositionDto> GetAsync(CancellationToken ct)
    {
        var key = $"librarycomposition:{currentUser.UserId}";
        if (cache.TryGetValue(key, out LibraryCompositionDto? cached) && cached is not null)
        {
            return cached;
        }

        var stats = await ComputeAsync(ct);
        cache.Set(key, stats, CacheFor);
        return stats;
    }

    private async Task<LibraryCompositionDto> ComputeAsync(CancellationToken ct)
    {
        var series = db.Series.AsNoTracking();
        var files = db.ChapterFiles.AsNoTracking();

        var totals = new LibraryCompositionTotalsDto(
            await series.CountAsync(ct),
            await series.CountAsync(s => s.MonitorNewItems != NewChapterMonitorMode.None, ct),
            await series.CountAsync(s => s.Status == SeriesStatus.Completed, ct),
            await db.Chapters.AsNoTracking().CountAsync(ct),
            await db.Chapters.AsNoTracking().CountAsync(c => c.ChapterFileId != null, ct),
            await files.CountAsync(ct),
            // Sum over an empty table is NULL in SQL, hence the nullable projection.
            await files.SumAsync(f => (long?)f.Size, ct) ?? 0);

        var byType = (await series
                .GroupBy(s => s.Type)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .Select(g => new NamedCountDto(
                string.IsNullOrWhiteSpace(g.Key) ? "Unknown" : g.Key, g.Count))
            .OrderByDescending(g => g.Count).ThenBy(g => g.Name)
            .ToList();

        var byStatus = (await series
                .GroupBy(s => s.Status)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .Select(g => new NamedCountDto(g.Key.ToString(), g.Count))
            .OrderByDescending(g => g.Count).ThenBy(g => g.Name)
            .ToList();

        var bySource = (await files
                .GroupBy(f => f.SourceName)
                .Select(g => new { g.Key, Files = g.Count(), Bytes = g.Sum(f => (long?)f.Size) ?? 0 })
                .ToListAsync(ct))
            .Select(g => new SourceUsageDto(
                string.IsNullOrWhiteSpace(g.Key) ? "Unknown" : g.Key, g.Files, g.Bytes))
            .OrderByDescending(g => g.Bytes).ThenBy(g => g.Name)
            .ToList();

        // Genres is a JSON list column, so the counting has to happen in memory — same constraint
        // RewindService works under. Added rides along rather than paying for a second scan.
        var shape = await series.Select(s => new { s.Added, s.Genres }).ToListAsync(ct);

        var genreCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in shape.SelectMany(s => s.Genres))
        {
            genreCounts[g] = genreCounts.GetValueOrDefault(g) + 1;
        }

        var topGenres = genreCounts
            .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
            .Take(TopGenreCount)
            .Select(kv => new NamedCountDto(kv.Key, kv.Value))
            .ToList();

        // UTC months, not the caller's calendar: this is a property of the library rather than of
        // anyone's reading day, and a shared number that shifted per viewer would be worse.
        var growth = new List<LibraryGrowthDto>();
        var running = 0;
        foreach (var month in shape
                     .GroupBy(s => s.Added.ToString("yyyy-MM"))
                     .OrderBy(g => g.Key))
        {
            running += month.Count();
            growth.Add(new LibraryGrowthDto(month.Key, month.Count(), running));
        }

        var largestRaw = await files
            .GroupBy(f => f.SeriesId)
            .Select(g => new { SeriesId = g.Key, Files = g.Count(), Bytes = g.Sum(f => (long?)f.Size) ?? 0 })
            .OrderByDescending(g => g.Bytes)
            .Take(LargestSeriesCount)
            .ToListAsync(ct);

        var largestIds = largestRaw.Select(g => g.SeriesId).ToList();
        var largestMeta = await series
            .Where(s => largestIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Title, s.CoverPath, s.LastMetadataRefresh })
            .ToDictionaryAsync(s => s.Id, ct);

        var largest = largestRaw
            .Where(g => largestMeta.ContainsKey(g.SeriesId))
            .Select(g =>
            {
                var meta = largestMeta[g.SeriesId];
                return new SeriesSizeDto(
                    g.SeriesId, meta.Title,
                    SeriesDto.CoverUrlFor(g.SeriesId, meta.CoverPath, meta.LastMetadataRefresh),
                    g.Files, g.Bytes);
            })
            .ToList();

        var byContentRating = (await series
                .GroupBy(s => s.ContentRating)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync(ct))
            // Null and "" both normalize to "unknown", so they need a second grouping pass rather
            // than a straight projection, which would keep them as two separate rows.
            .GroupBy(g => string.IsNullOrWhiteSpace(g.Key) ? "unknown" : g.Key)
            .Select(g => new NamedCountDto(g.Key, g.Sum(x => x.Count)))
            .OrderByDescending(g => g.Count).ThenBy(g => g.Name)
            .ToList();

        var sourceReliability = await GetSourceReliabilityAsync(ct);
        var requests = await GetRequestSummaryAsync(ct);
        var monitorCatches = await GetMonitorCatchesAsync(ct);

        return new LibraryCompositionDto(
            totals, byType, byStatus, bySource, topGenres, growth, largest,
            byContentRating, sourceReliability, requests, monitorCatches);
    }

    // HousekeepingJob deletes Completed/Cancelled DownloadQueue rows after 30 days and keeps Failed,
    // so a wider window would silently undercount completions relative to failures.
    private async Task<IReadOnlyList<SourceReliabilityDto>> GetSourceReliabilityAsync(CancellationToken ct)
    {
        var since = DateTime.UtcNow - ReliabilityWindow;

        var rows = await db.DownloadQueue
            .AsNoTracking()
            .Where(q => q.QueuedAt >= since &&
                        (q.Status == QueueStatus.Completed || q.Status == QueueStatus.Failed))
            .Select(q => new
            {
                Name = q.SourceMapping != null ? q.SourceMapping.SourceName : null,
                q.Protocol,
                q.Status,
                q.QueuedAt,
                q.CompletedAt
            })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => string.IsNullOrWhiteSpace(r.Name) ? r.Protocol.ToString().ToLowerInvariant() : r.Name)
            .Select(g =>
            {
                var completed = g.Where(r => r.Status == QueueStatus.Completed).ToList();
                var failed = g.Count(r => r.Status == QueueStatus.Failed);

                var durations = completed
                    .Where(r => r.CompletedAt.HasValue)
                    .Select(r => (r.CompletedAt!.Value - r.QueuedAt).TotalSeconds)
                    .OrderBy(s => s)
                    .ToList();

                return new SourceReliabilityDto(
                    g.Key, completed.Count, failed,
                    durations.Count == 0 ? null : (int)Median(durations));
            })
            .OrderByDescending(r => r.Completed + r.Failed).ThenBy(r => r.Name)
            .ToList();
    }

    private async Task<RequestSummaryDto> GetRequestSummaryAsync(CancellationToken ct)
    {
        var isAdmin = currentUser.Has(MakiPermission.Admin);
        var source = isAdmin ? db.SeriesRequests.IgnoreQueryFilters() : db.SeriesRequests;

        var open = await source.AsNoTracking()
            .CountAsync(r => r.Status == SeriesRequestStatus.Pending || r.Status == SeriesRequestStatus.Processing, ct);

        var since = DateTime.UtcNow - RequestResolvedWindow;
        var resolved = await source.AsNoTracking()
            .Where(r => r.ResolvedAt != null && r.ResolvedAt >= since)
            .Select(r => new { r.Created, r.ResolvedAt })
            .ToListAsync(ct);

        var hours = resolved
            .Select(r => (r.ResolvedAt!.Value - r.Created).TotalHours)
            .OrderBy(h => h)
            .ToList();

        return new RequestSummaryDto(
            isAdmin, open, resolved.Count,
            hours.Count == 0 ? null : Median(hours));
    }

    private async Task<int> GetMonitorCatchesAsync(CancellationToken ct)
    {
        var since = DateTime.UtcNow - ReliabilityWindow;
        return await db.DownloadQueue
            .AsNoTracking()
            .CountAsync(q =>
                q.Origin == DownloadOrigin.MonitorRefresh &&
                q.Status == QueueStatus.Completed &&
                q.CompletedAt != null && q.QueuedAt >= since, ct);
    }

    private static double Median(List<double> sorted)
    {
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2 : sorted[mid];
    }
}
