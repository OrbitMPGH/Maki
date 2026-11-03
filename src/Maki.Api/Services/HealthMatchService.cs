using Maki.Core.Entities;
using Maki.Core.Parsing;
using Maki.Core.Paths;
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

    /// <param name="LabelKind">
    /// What the file name parsed to: <c>"chapter"</c>, <c>"volume"</c>, <c>"volumes"</c> (a range) or
    /// <c>"unrecognized"</c>. A plain discriminator rather than rendered text, because the frontend
    /// needs the label in two grammatical forms - title case for the badge, lower case mid-sentence -
    /// and lowercasing a translation is wrong for a language that capitalizes the noun regardless of
    /// position. <see cref="Number"/> is the chapter number or the volume's start.
    /// </param>
    public record UnlinkedMatch(
        bool Recognized, string LabelKind, decimal? Number, int? VolumeEnd, int? SeriesId, string? SeriesTitle,
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
        var (labelKind, number, volumeEnd) = Label(parsed);
        if (owner == null)
            return new UnlinkedMatch(parsed.IsRecognized, labelKind, number, volumeEnd, null, null, [], []);

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

        return new UnlinkedMatch(parsed.IsRecognized, labelKind, number, volumeEnd, owner.Id, owner.Title,
            matched.Select(c => new MatchChapter(c.Id, c.Number, c.Title, c.ChapterFileId != null)).ToList(),
            counterparts);
    }

    /// <summary>What to do about an unlinked archive, decided the way <see cref="MatchAsync"/> decides it.</summary>
    public static class Cases
    {
        /// <summary>A missing file's record most likely belongs here; the Missing filter relinks it.</summary>
        public const string Moved = "moved";
        /// <summary>Byte for byte another inventoried file; the Duplicate filter handles it.</summary>
        public const string Identical = "identical";
        /// <summary>Not inside any series folder, so nothing to import it into.</summary>
        public const string Outside = "outside";
        public const string Unrecognized = "unrecognized";
        /// <summary>Names a chapter or volume the series does not have yet.</summary>
        public const string NoChapter = "noChapter";
        /// <summary>A chapter it names already has a different file; needs a comparison.</summary>
        public const string Rival = "rival";
        /// <summary>Nothing holds what it names: importing links it, nothing to compare.</summary>
        public const string Import = "import";
    }

    public sealed record Classified(int Id, string Version, string RelativePath, string Case, string? Folder);

    /// <summary>
    /// <see cref="MatchAsync"/>'s answer for many files at once, in a handful of queries, so a list can
    /// count every case across the filter instead of one page of it.
    /// </summary>
    public async Task<List<Classified>> ClassifyAsync(IQueryable<HealthFile> unlinked, CancellationToken ct)
    {
        var files = await unlinked.AsNoTracking().Where(f => f.ChapterFileId == null)
            .Select(f => new { f.Id, f.Version, f.RootFolderId, f.RelativePath, f.ContentHash }).ToListAsync(ct);
        if (files.Count == 0) return [];
        var rootIds = files.Select(f => f.RootFolderId).Distinct().ToList();
        var series = await db.Series.AsNoTracking().Where(s => rootIds.Contains(s.RootFolderId))
            .Select(s => new { s.Id, s.RootFolderId, s.FolderName }).ToListAsync(ct);
        int? OwnerOf(int rootId, string path) => series
            .Where(s => s.RootFolderId == rootId && Contains(s.FolderName, path))
            .OrderByDescending(s => s.FolderName.Length).Select(s => (int?)s.Id).FirstOrDefault();
        var owners = files.ToDictionary(f => f.Id, f => OwnerOf(f.RootFolderId, f.RelativePath));
        var ownerIds = owners.Values.Where(o => o != null).Select(o => o!.Value).Distinct().ToList();
        var chapters = (await db.Chapters.AsNoTracking().Where(c => ownerIds.Contains(c.SeriesId))
                .Select(c => new { c.SeriesId, c.Number, c.Volume, c.ChapterFileId }).ToListAsync(ct))
            .ToLookup(c => c.SeriesId);
        var hashes = files.Where(f => f.ContentHash != null).Select(f => f.ContentHash!).Distinct().ToList();
        var twinned = (await db.HealthFiles.AsNoTracking().Where(f => !f.Removed && f.ContentHash != null && hashes.Contains(f.ContentHash))
                .GroupBy(f => f.ContentHash!).Where(g => g.Count() > 1).Select(g => g.Key).ToListAsync(ct))
            .ToHashSet();
        var missingIds = await db.HealthFiles.AsNoTracking()
            .Where(f => !f.Removed && f.Size < 0 && f.ChapterFileId != null && rootIds.Contains(f.RootFolderId))
            .Select(f => f.Id).ToListAsync(ct);
        var movedTargets = (await HealthMovedFiles.FindAsync(db, missingIds, ct)).Values.Select(m => m.TargetId).ToHashSet();

        return files.Select(f =>
        {
            var owner = owners[f.Id];
            var parsed = ReleaseNameParser.ParseFileName(f.RelativePath);
            string kind;
            if (movedTargets.Contains(f.Id)) kind = Cases.Moved;
            else if (f.ContentHash != null && twinned.Contains(f.ContentHash)) kind = Cases.Identical;
            else if (owner == null) kind = Cases.Outside;
            else if (!parsed.IsRecognized) kind = Cases.Unrecognized;
            else
            {
                var matched = chapters[owner.Value].Where(c => parsed.IsChapter
                    ? c.Number == parsed.Number
                    : parsed.IsVolume && c.Volume != null && c.Volume >= parsed.Volume && c.Volume <= (parsed.VolumeEnd ?? parsed.Volume)).ToList();
                kind = matched.Count == 0 ? Cases.NoChapter : matched.Any(c => c.ChapterFileId != null) ? Cases.Rival : Cases.Import;
            }
            return new Classified(f.Id, f.Version, f.RelativePath, kind, kind == Cases.Outside ? LibraryPaths.TopFolder(f.RelativePath) : null);
        }).ToList();
    }

    internal static bool Contains(string folderName, string relativePath) =>
        folderName.Length > 0 && relativePath.Length > folderName.Length &&
        relativePath.StartsWith(folderName, Compare) &&
        (relativePath[folderName.Length] == Path.DirectorySeparatorChar || relativePath[folderName.Length] == '/');

    private static (string Kind, decimal? Number, int? VolumeEnd) Label(ParsedReleaseFile parsed) =>
        parsed.IsChapter ? ("chapter", parsed.Number, null)
        : parsed.IsVolume ? parsed.VolumeEnd is { } end ? ("volumes", parsed.Volume, end) : ("volume", parsed.Volume, null)
        : ("unrecognized", null, null);
}
