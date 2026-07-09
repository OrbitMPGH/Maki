using Mangarr.Core.Entities;
using Mangarr.Core.Parsing;
using Mangarr.Data;
using Microsoft.EntityFrameworkCore;

namespace Mangarr.Api.Services;

/// <summary>
/// Shared logic for adopting CBZ files that Mangarr didn't download page-by-page
/// (library imports, completed torrents): create ChapterFile records and link
/// chapters by parsed number, or by volume range when volume data exists.
/// </summary>
public class CbzLinkService(MangarrDbContext db)
{
    /// <param name="files">Absolute paths of CBZ files, already inside the series folder.</param>
    /// <param name="seriesDir">Absolute path of the series folder (for relative paths).</param>
    public async Task<(int Linked, int Unrecognized)> LinkFilesAsync(
        Series series, string seriesDir, IEnumerable<string> files, string sourceName, CancellationToken ct = default)
    {
        var chapters = await db.Chapters.Where(c => c.SeriesId == series.Id).ToListAsync(ct);
        var linked = 0;
        var unrecognized = 0;

        foreach (var file in files.OrderBy(f => f))
        {
            var parsed = ReleaseNameParser.ParseFileName(file);
            var relativePath = Path.GetRelativePath(seriesDir, file);

            var chapterFile = new ChapterFile
            {
                SeriesId = series.Id,
                RelativePath = Path.Combine(series.FolderName, relativePath),
                Size = new FileInfo(file).Length,
                SourceName = sourceName,
                DateAdded = DateTime.UtcNow
            };
            db.ChapterFiles.Add(chapterFile);
            await db.SaveChangesAsync(ct); // need the file id for linking

            List<Chapter> targets = [];
            if (parsed.IsChapter)
            {
                var match = chapters.FirstOrDefault(c => c.Number == parsed.Number && c.ChapterFileId == null)
                            ?? chapters.FirstOrDefault(c => c.Number == parsed.Number);
                if (match != null)
                {
                    targets.Add(match);
                }
            }
            else if (parsed.IsVolume)
            {
                var end = parsed.VolumeEnd ?? parsed.Volume;
                targets = chapters
                    .Where(c => c.Volume >= parsed.Volume && c.Volume <= end && c.ChapterFileId == null)
                    .ToList();
            }
            else
            {
                unrecognized++;
            }

            foreach (var chapter in targets)
            {
                chapter.ChapterFileId = chapterFile.Id;
            }

            if (targets.Count > 0)
            {
                linked++;
            }
        }

        await db.SaveChangesAsync(ct);
        return (linked, unrecognized);
    }
}
