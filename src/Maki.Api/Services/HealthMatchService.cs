using System.Globalization;
using Maki.Core.Entities;
using Maki.Core.Parsing;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Works out what an archive with no <see cref="ChapterFile"/> record would have linked to.
/// <para>
/// An "unlinked" finding on its own says nothing about what to do next. Two very different
/// situations produce it: the chapter already has a file and this archive is a second copy
/// competing with it, or nothing holds that chapter and the archive is simply waiting to be
/// imported. The first needs a comparison, the second needs one click, so the reviewer has to be
/// told which one they are looking at.
/// </para>
/// <para>
/// Everything here is read-only inference from names and folder layout - the same rules the
/// importer uses (<see cref="ReleaseNameParser"/>, series folder containment). It never writes,
/// and a wrong guess costs the reviewer a bad suggestion, never a linked file.
/// </para>
/// </summary>
public class HealthMatchService(MakiDbContext db)
{
    private static readonly StringComparison Compare =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public record MatchChapter(int Id, decimal? Number, string? Title, bool HasFile);

    /// <param name="HealthFileId">Null when the rival file has never been inventoried, which is
    /// the only case where the two archives cannot be compared page by page.</param>
    /// <param name="PixelHeight">Total height of every page stacked. Page count is not comparable
    /// across long-strip releases - two sites cut the same chapter into different numbers of
    /// slices - but the height of the strip they add up to is.</param>
    public record MatchCounterpart(
        int ChapterFileId, string RelativePath, long Size, string SourceName,
        int? HealthFileId, string? Version, string? Status, int Pages, long PixelHeight,
        string? ContentHash, List<int> ChapterIds);

    public record UnlinkedMatch(
        bool Recognized, string Label, int? SeriesId, string? SeriesTitle,
        List<MatchChapter> Chapters, List<MatchCounterpart> Counterparts);

    /// <summary>
    /// The series whose folder contains this archive, or null when it sits outside every series
    /// folder in its root. Longest folder name wins, so a series nested inside another one's
    /// folder still claims its own files.
    /// </summary>
    public async Task<Series?> OwnerAsync(HealthFile file, CancellationToken ct)
    {
        var candidates = await db.Series.Include(s => s.RootFolder)
            .Where(s => s.RootFolderId == file.RootFolderId).ToListAsync(ct);
        return candidates.Where(s => Contains(s.FolderName, file.RelativePath))
            .OrderByDescending(s => s.FolderName.Length).FirstOrDefault();
    }

    public async Task<UnlinkedMatch?> MatchAsync(HealthFile file, CancellationToken ct)
    {
        if (file.ChapterFileId != null) return null;
        var parsed = ReleaseNameParser.ParseFileName(file.RelativePath);
        var owner = await OwnerAsync(file, ct);
        var label = Label(parsed);
        if (owner == null)
            return new UnlinkedMatch(parsed.IsRecognized, label, null, null, [], []);

        var chapters = await db.Chapters.Where(c => c.SeriesId == owner.Id).ToListAsync(ct);
        var matched = parsed.IsChapter
            ? chapters.Where(c => c.Number == parsed.Number).ToList()
            : parsed.IsVolume
                ? chapters.Where(c => c.Volume != null && c.Volume >= parsed.Volume &&
                                      c.Volume <= (parsed.VolumeEnd ?? parsed.Volume)).ToList()
                : [];

        var counterparts = new List<MatchCounterpart>();
        foreach (var chapterFileId in matched.Where(c => c.ChapterFileId != null)
                     .Select(c => c.ChapterFileId!.Value).Distinct())
        {
            var record = await db.ChapterFiles.FindAsync([chapterFileId], ct);
            if (record == null) continue;
            var inventoried = await db.HealthFiles
                .FirstOrDefaultAsync(f => f.ChapterFileId == chapterFileId && !f.Removed, ct);
            var analysis = inventoried == null ? null : HealthScanService.Analysis(inventoried);
            counterparts.Add(new MatchCounterpart(
                chapterFileId, record.RelativePath, record.Size, record.SourceName,
                inventoried?.Id, inventoried?.Version, inventoried?.Status,
                analysis?.Pages.Count ?? 0, analysis?.Pages.Sum(p => (long)p.Height) ?? 0,
                inventoried?.ContentHash,
                matched.Where(c => c.ChapterFileId == chapterFileId).Select(c => c.Id).ToList()));
        }

        return new UnlinkedMatch(parsed.IsRecognized, label, owner.Id, owner.Title,
            matched.Select(c => new MatchChapter(c.Id, c.Number, c.Title, c.ChapterFileId != null)).ToList(),
            counterparts);
    }

    private static bool Contains(string folderName, string relativePath) =>
        folderName.Length > 0 && relativePath.Length > folderName.Length &&
        relativePath.StartsWith(folderName, Compare) &&
        (relativePath[folderName.Length] == Path.DirectorySeparatorChar || relativePath[folderName.Length] == '/');

    private static string Label(ParsedReleaseFile parsed) =>
        parsed.IsChapter ? $"Chapter {parsed.Number!.Value.ToString("0.###", CultureInfo.InvariantCulture)}"
        : parsed.IsVolume ? parsed.VolumeEnd is { } end ? $"Volumes {parsed.Volume}-{end}" : $"Volume {parsed.Volume}"
        : "Unrecognized name";
}
