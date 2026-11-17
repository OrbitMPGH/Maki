using Maki.Core.Download;
using Maki.Core.Images;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

public sealed record ChapterPageHashes(
    string Mapping, decimal Number, string SourceChapterId, int Pages, IReadOnlyList<string?> Hashes, IReadOnlyList<string?>? Thumbs);

/// <summary>
/// Temporary, for checking fold decisions against the pages themselves: fetches one source chapter's
/// pages through its source and returns a perceptual hash per page. Nothing is kept.
/// </summary>
public class ChapterFoldAuditService(
    MakiDbContext db,
    SourceRegistry sourceRegistry,
    PageDownloader pageDownloader,
    ILogger<ChapterFoldAuditService> logger)
{
    public async Task<ChapterPageHashes?> HashAsync(int seriesId, string mapping, decimal number, bool thumbs, CancellationToken ct)
    {
        var mappings = await db.SourceMappings.AsNoTracking()
            .Where(m => m.SeriesId == seriesId)
            .OrderBy(m => m.Priority).ThenBy(m => m.Id)
            .ToListAsync(ct);
        var target = mappings.FirstOrDefault(m =>
            (mappings.Count(o => o.SourceName == m.SourceName) > 1 ? $"{m.SourceName}#{m.Id}" : m.SourceName) == mapping);
        if (target is null || sourceRegistry.Find(target.SourceName) is not { } source)
        {
            return null;
        }

        var link = await db.ChapterSourceLinks.AsNoTracking()
            .Where(l => l.SourceMappingId == target.Id && l.Chapter!.Number == number)
            .OrderBy(l => l.Chapter!.Language == "en" ? 0 : 1)
            .Select(l => new { l.SourceChapterId, l.NumberRaw, l.Volume, l.Title, l.Chapter!.Language, l.ReleaseDate, l.Url })
            .FirstOrDefaultAsync(ct);
        if (link is null)
        {
            return null;
        }

        var chapter = new SourceChapter(source.Name, target.SourceSeriesId, link.SourceChapterId, link.NumberRaw, number,
            link.Volume, link.Title, link.Language, link.ReleaseDate, link.Url);
        var pages = await source.GetPagesAsync(chapter, ct);
        var dir = Path.Combine(Path.GetTempPath(), "maki-fold-audit", Guid.NewGuid().ToString("N"));
        try
        {
            var files = await pageDownloader.DownloadAsync(pages, source.Name, dir, null, ct, source as IPageClientSource);
            var hashes = new List<string?>();
            var small = new List<string?>();
            foreach (var file in files)
            {
                if (thumbs)
                {
                    try
                    {
                        small.Add(Convert.ToBase64String(ImageCodec.JpegThumbnail(await File.ReadAllBytesAsync(file, ct), 120, 70, false)));
                    }
                    catch (Exception)
                    {
                        small.Add(null);
                    }
                }

                try
                {
                    hashes.Add(PerceptualHash.OfFile(file).ToString("x16"));
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Could not hash {File}", file);
                    hashes.Add(null);
                }
            }

            return new ChapterPageHashes(mapping, number, link.SourceChapterId, files.Count, hashes, thumbs ? small : null);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
