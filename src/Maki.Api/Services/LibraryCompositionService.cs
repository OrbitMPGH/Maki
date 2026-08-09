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

        return new LibraryCompositionDto(totals, byType, byStatus, bySource, topGenres, growth, largest);
    }
}
