using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Gives every source mapping the track record its library files already hold, once. Files
/// measured from now on are recorded as they are measured; this covers the ones measured before
/// <see cref="SourceQualitySample"/> existed. A sample is dated by when its file was added, so an
/// old library reads as old evidence and the upgrade scan still probes before trusting it.
/// </summary>
public class SourceQualitySeeder(MakiDbContext db, ILogger<SourceQualitySeeder> logger)
{
    public const string MarkerKey = "upgrades.sourceQualitySeeded";

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        if (await db.AppConfig.AnyAsync(c => c.Key == MarkerKey, ct))
        {
            return;
        }

        var files = await db.Chapters.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.ChapterFileId != null && c.ChapterFile!.MeasuredAtUtc != null && c.ChapterFile.SourceName != null)
            .Select(c => new
            {
                ChapterId = c.Id,
                c.ChapterFile!.SeriesId,
                c.ChapterFile.SourceName,
                c.ChapterFile.PageCount,
                c.ChapterFile.MedianWidth,
                c.ChapterFile.MedianHeight,
                c.ChapterFile.Size,
                c.ChapterFile.ImageFormat,
                c.ChapterFile.DateAdded
            })
            .ToListAsync(ct);
        var mappings = (await db.SourceMappings.IgnoreQueryFilters().AsNoTracking().ToListAsync(ct))
            .GroupBy(m => (m.SeriesId, m.SourceName.ToLowerInvariant()))
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.Id).First());

        var seeded = 0;
        foreach (var group in files
                     .Where(f => f is { PageCount: > 0, MedianWidth: > 0, MedianHeight: > 0, Size: > 0 })
                     .GroupBy(f => mappings.GetValueOrDefault((f.SeriesId, f.SourceName!.ToLowerInvariant()))))
        {
            if (group.Key is not { } mapping)
            {
                continue;
            }

            foreach (var f in group.DistinctBy(f => f.ChapterId).OrderByDescending(f => f.DateAdded).Take(SourceQualitySamples.Keep))
            {
                db.SourceQualitySamples.Add(new SourceQualitySample
                {
                    SourceMappingId = mapping.Id,
                    SeriesId = mapping.SeriesId,
                    ChapterId = f.ChapterId,
                    Origin = SourceQualityOrigin.Library,
                    PageCount = f.PageCount!.Value,
                    MedianWidth = f.MedianWidth!.Value,
                    MedianHeight = f.MedianHeight!.Value,
                    SizeBytes = f.Size,
                    ImageFormat = f.ImageFormat,
                    MeasuredAtUtc = f.DateAdded
                });
                seeded++;
            }
        }

        db.AppConfig.Add(new AppConfigEntry { Key = MarkerKey, Value = DateTime.UtcNow.ToString("O") });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Seeded {Count} source quality samples from measured library files", seeded);
    }
}
