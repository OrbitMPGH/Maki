using Maki.Core.Entities;
using Maki.Core.Quality;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Each source mapping's measured track record. Every write site that has just measured a copy from
/// a known mapping records it here; <see cref="EstimatesAsync"/> turns the recent ones into a
/// <see cref="SourceQualityEstimate"/>.
/// </summary>
public static class SourceQualitySamples
{
    /// <summary>Samples kept per mapping; older ones are dropped as new ones arrive.</summary>
    public const int Keep = 10;

    /// <summary>
    /// Adds or replaces the sample for this mapping and chapter, unsaved. A copy with no measured
    /// width, height, pages or size says nothing about the source and is ignored.
    /// </summary>
    public static async Task RecordAsync(MakiDbContext db, SourceMapping mapping, int chapterId,
        SourceQualityOrigin origin, int? pageCount, int? medianWidth, int? medianHeight, long? sizeBytes,
        string? imageFormat, DateTime nowUtc, CancellationToken ct)
    {
        if (pageCount is not > 0 || medianWidth is not > 0 || medianHeight is not > 0 || sizeBytes is not > 0)
        {
            return;
        }

        var sample = db.SourceQualitySamples.Local.FirstOrDefault(Match)
                     ?? await db.SourceQualitySamples.IgnoreQueryFilters()
                         .FirstOrDefaultAsync(s => s.SourceMappingId == mapping.Id && s.ChapterId == chapterId, ct);
        var added = sample is null;
        if (sample is null)
        {
            sample = new SourceQualitySample { SourceMappingId = mapping.Id, SeriesId = mapping.SeriesId, ChapterId = chapterId };
            db.SourceQualitySamples.Add(sample);
        }

        sample.Origin = origin;
        sample.PageCount = pageCount.Value;
        sample.MedianWidth = medianWidth.Value;
        sample.MedianHeight = medianHeight.Value;
        sample.SizeBytes = sizeBytes.Value;
        sample.ImageFormat = imageFormat;
        sample.MeasuredAtUtc = nowUtc;
        if (added)
        {
            await PruneAsync(db, mapping.Id, ct);
        }

        return;

        bool Match(SourceQualitySample s) => s.SourceMappingId == mapping.Id && s.ChapterId == chapterId;
    }

    /// <summary>Every mapping of <paramref name="seriesId"/> with at least one usable sample.</summary>
    public static async Task<Dictionary<int, SourceQualityEstimate>> EstimatesAsync(
        MakiDbContext db, int seriesId, CancellationToken ct)
    {
        var samples = await db.SourceQualitySamples.AsNoTracking()
            .Where(s => s.SeriesId == seriesId)
            .ToListAsync(ct);
        var estimates = new Dictionary<int, SourceQualityEstimate>();
        foreach (var group in samples.GroupBy(s => s.SourceMappingId))
        {
            if (SourceQualityEstimate.From([.. group.OrderByDescending(s => s.MeasuredAtUtc).Take(Keep)]) is { } estimate)
            {
                estimates[group.Key] = estimate;
            }
        }

        return estimates;
    }

    /// <summary>
    /// Each source's record across every series, newest <paramref name="perSource"/> samples, for the
    /// global source list. Keyed by source name, case-insensitive.
    /// </summary>
    public static async Task<Dictionary<string, (SourceQualityEstimate Estimate, int Series)>> LibraryEstimatesAsync(
        MakiDbContext db, CancellationToken ct, int perSource = 200)
    {
        var samples = await db.SourceQualitySamples.IgnoreQueryFilters().AsNoTracking()
            .Join(db.SourceMappings.IgnoreQueryFilters(), s => s.SourceMappingId, m => m.Id,
                (s, m) => new { Sample = s, m.SourceName })
            .ToListAsync(ct);
        var result = new Dictionary<string, (SourceQualityEstimate, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in samples.GroupBy(x => x.SourceName, StringComparer.OrdinalIgnoreCase))
        {
            var newest = group.Select(x => x.Sample).OrderByDescending(s => s.MeasuredAtUtc).Take(perSource).ToList();
            if (SourceQualityEstimate.From(newest) is { } estimate)
            {
                result[group.Key] = (estimate, newest.Select(s => s.SeriesId).Distinct().Count());
            }
        }

        return result;
    }

    /// <summary>
    /// Keeps the newest <see cref="Keep"/> of the mapping's samples. Sorted on the tracked values, so a
    /// sample refreshed or added earlier in the same unsaved batch counts as new.
    /// </summary>
    private static async Task PruneAsync(MakiDbContext db, int mappingId, CancellationToken ct)
    {
        var saved = await db.SourceQualitySamples.IgnoreQueryFilters()
            .Where(s => s.SourceMappingId == mappingId)
            .ToListAsync(ct);
        var stale = saved
            .Concat(db.SourceQualitySamples.Local.Where(s => s.SourceMappingId == mappingId))
            .Distinct()
            .Where(s => db.Entry(s).State != EntityState.Deleted)
            .OrderByDescending(s => s.MeasuredAtUtc)
            .ThenByDescending(s => db.Entry(s).State == EntityState.Added)
            .ThenByDescending(s => s.Id)
            .Skip(Keep)
            .ToList();
        db.SourceQualitySamples.RemoveRange(stale);
    }
}
