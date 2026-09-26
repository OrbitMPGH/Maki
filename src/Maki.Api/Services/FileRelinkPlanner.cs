using System.Globalization;
using Maki.Core.Entities;
using Maki.Core.Parsing;
using Maki.Core.Paths;
using Maki.Core.Reading;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>How the planner decided a file contains a chapter, strongest first.</summary>
public enum RelinkConfidence
{
    /// <summary>The chapter marker is in the volume archive's page file names.</summary>
    PageMarkers,
    /// <summary>The chapter's provider volume falls inside the file name's volume range.</summary>
    VolumeRange,
    /// <summary>A single-chapter file whose name carries the number.</summary>
    FileName,
    /// <summary>Proportional guess for a finished series whose volume files cover every volume.</summary>
    Estimated,
}

/// <param name="Confidence">Strongest confidence among the chapters the file ends up with, or null.</param>
/// <param name="Chapters">Chapter labels the file backs after the plan.</param>
/// <param name="Gains">Of those, chapters moving onto it.</param>
/// <param name="Loses">Chapters it backs today that move elsewhere.</param>
/// <param name="Superseded">Backs nothing after the plan and every chapter it could hold lives on another file.</param>
public record RelinkPlanFile(
    string RelativePath,
    string FileName,
    long Size,
    string? Label,
    bool IsVolume,
    bool Recognized,
    string? Confidence,
    List<string> Chapters,
    List<string> Gains,
    List<string> Loses,
    bool Superseded);

public record RelinkPlan(
    int SeriesId,
    List<RelinkPlanFile> Files,
    int Moved,
    int SupersededCount,
    long SupersededBytes,
    int Unrecognized);

public record RelinkResult(int Moved, int Superseded, int Deleted, int Failed, long FreedBytes);

/// <summary>
/// Rebuilds a series' chapter-to-file map from what is on disk, volumes first. The incremental
/// linker (<see cref="CbzLinkService"/>) hands each chapter to whichever file arrived first, so a
/// library holding both an omnibus and the single chapters it contains ends up split between the
/// two depending on adoption order. This planner ignores history: every chapter goes to the best
/// file that provably contains it, and a single-chapter file left backing nothing whose chapter
/// now sits on a volume is reported as superseded so its space can be reclaimed.
/// </summary>
public class FileRelinkPlanner(
    MakiDbContext db, ReaderArchiveCache archives, KavitaScanService kavitaScans, ILogger<FileRelinkPlanner> logger)
{
    private sealed class Candidate
    {
        public required string RelativePath { get; init; }
        public required string AbsolutePath { get; init; }
        public required long Size { get; init; }
        public required ParsedReleaseFile Parsed { get; init; }
        public ChapterFile? Record { get; set; }
        /// <summary>Chapters this file contains and how sure we are, keyed by chapter id.</summary>
        public Dictionary<int, RelinkConfidence> Covers { get; } = [];
        public int Span => Parsed.IsVolume ? (Parsed.VolumeEnd ?? Parsed.Volume!.Value) - Parsed.Volume!.Value : 0;
    }

    private sealed record Built(
        Series Series,
        List<Chapter> Chapters,
        List<ChapterFile> Records,
        List<Candidate> Candidates,
        Dictionary<int, Candidate?> Assignment,
        Dictionary<int, Candidate> CandidateByRecordId);

    public async Task<RelinkPlan> PlanAsync(Series series, CancellationToken ct = default)
    {
        var built = await BuildAsync(series, ct);
        return ToPlan(built);
    }

    public async Task<RelinkResult> ApplyAsync(Series series, bool deleteSuperseded, CancellationToken ct = default)
    {
        var built = await BuildAsync(series, ct);
        var plan = ToPlan(built);
        var rootPath = series.RootFolder!.Path;

        // Records first: a file adopted here needs an id before a chapter can point at it.
        foreach (var candidate in built.Candidates.Where(c => c.Record is null))
        {
            if (!built.Assignment.Values.Any(a => ReferenceEquals(a, candidate)))
            {
                continue;
            }

            candidate.Record = new ChapterFile
            {
                SeriesId = series.Id,
                RelativePath = candidate.RelativePath,
                Size = candidate.Size,
                SourceName = "relink",
                DateAdded = DateTime.UtcNow,
            };
            db.ChapterFiles.Add(candidate.Record);
        }

        await db.SaveChangesAsync(ct);

        var moved = 0;
        var touched = new HashSet<int>();
        foreach (var chapter in built.Chapters)
        {
            if (!built.Assignment.TryGetValue(chapter.Id, out var target) || target?.Record is null)
            {
                continue;
            }

            if (chapter.ChapterFileId == target.Record.Id)
            {
                continue;
            }

            if (chapter.ChapterFileId is { } old)
            {
                touched.Add(old);
            }

            chapter.ChapterFileId = target.Record.Id;
            touched.Add(target.Record.Id);
            if (target.Parsed.VolumeEnd is null && target.Parsed.Volume is { } volume)
            {
                chapter.Volume ??= volume;
            }

            moved++;
        }

        var deleted = 0;
        var failed = 0;
        long freed = 0;
        var supersededPaths = plan.Files.Where(f => f.Superseded).Select(f => f.RelativePath).ToHashSet(StringComparer.Ordinal);
        if (deleteSuperseded)
        {
            foreach (var candidate in built.Candidates.Where(c => supersededPaths.Contains(c.RelativePath)))
            {
                try
                {
                    File.Delete(candidate.AbsolutePath);
                }
                catch (DirectoryNotFoundException)
                {
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Could not delete superseded file {File}", candidate.RelativePath);
                    failed++;
                    continue;
                }

                if (candidate.Record is not null)
                {
                    archives.Invalidate(candidate.Record.Id);
                    db.ChapterFiles.Remove(candidate.Record);
                }

                freed += candidate.Size;
                deleted++;
            }
        }

        await db.SaveChangesAsync(ct);
        foreach (var id in touched)
        {
            archives.Invalidate(id);
        }

        if (moved > 0 || deleted > 0)
        {
            kavitaScans.QueueScan(Path.Combine(rootPath, series.FolderName), series.Id);
            logger.LogInformation(
                "Relinked '{Title}': {Moved} chapter link(s) moved, {Superseded} file(s) superseded, {Deleted} deleted",
                series.Title, moved, plan.SupersededCount, deleted);
        }

        return new RelinkResult(moved, plan.SupersededCount, deleted, failed, freed);
    }

    private async Task<Built> BuildAsync(Series series, CancellationToken ct)
    {
        var rootFolder = series.RootFolder ?? throw new InvalidOperationException("Series has no root folder loaded");
        var chapters = await db.Chapters.Where(c => c.SeriesId == series.Id).ToListAsync(ct);
        var records = await db.ChapterFiles.Where(f => f.SeriesId == series.Id).ToListAsync(ct);
        var recordByKey = new Dictionary<string, ChapterFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            recordByKey.TryAdd(LibraryPaths.ComparisonKey(record.RelativePath), record);
        }

        var candidates = new List<Candidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in await SeriesFolders.ForAsync(db, series, ct))
        {
            var seriesDir = Path.Combine(rootFolder.Path, folder);
            if (!Directory.Exists(seriesDir))
            {
                continue;
            }

            foreach (var file in Directory.GetFiles(seriesDir, "*", SearchOption.AllDirectories)
                         .Where(ComicFile.IsComic).OrderBy(f => f, StringComparer.Ordinal))
            {
                var relativePath = Path.Combine(folder, Path.GetRelativePath(seriesDir, file));
                var key = LibraryPaths.ComparisonKey(relativePath);
                if (!seen.Add(key))
                {
                    continue;
                }

                var record = recordByKey.GetValueOrDefault(key);
                candidates.Add(new Candidate
                {
                    RelativePath = record?.RelativePath ?? relativePath,
                    AbsolutePath = file,
                    Size = new FileInfo(file).Length,
                    Parsed = ReleaseNameParser.ParseFileName(file),
                    Record = record,
                });
            }
        }

        var estimator = CompletedSeriesEstimator(series, candidates);
        foreach (var candidate in candidates)
        {
            Cover(candidate, chapters, estimator);
        }

        var candidateByRecordId = candidates
            .Where(c => c.Record is not null)
            .ToDictionary(c => c.Record!.Id);

        var assignment = new Dictionary<int, Candidate?>();
        foreach (var chapter in chapters)
        {
            var current = chapter.ChapterFileId is { } id ? candidateByRecordId.GetValueOrDefault(id) : null;
            var best = candidates
                .Where(c => c.Covers.ContainsKey(chapter.Id))
                .OrderBy(c => Rank(c.Covers[chapter.Id]))
                .ThenBy(c => c.Span)
                // A tie keeps the file the chapter is on, so applying a plan twice changes nothing.
                .ThenBy(c => ReferenceEquals(c, current) ? 0 : 1)
                .ThenBy(c => c.RelativePath, StringComparer.Ordinal)
                .FirstOrDefault();
            if (best is not null)
            {
                assignment[chapter.Id] = best;
            }
        }

        return new Built(series, chapters, records, candidates, assignment, candidateByRecordId);
    }

    private static int Rank(RelinkConfidence confidence) => confidence switch
    {
        RelinkConfidence.PageMarkers => 0,
        RelinkConfidence.VolumeRange => 1,
        // A single file named for its chapter is certain about that chapter; a proportional guess
        // for which volume holds it is not, so the guess only wins when nothing else has it.
        RelinkConfidence.FileName => 2,
        _ => 3,
    };

    private static void Cover(Candidate candidate, List<Chapter> chapters, Func<Chapter, int?>? estimator)
    {
        var parsed = candidate.Parsed;
        if (parsed.IsChapter)
        {
            foreach (var chapter in chapters.Where(c => c.Number == parsed.Number))
            {
                candidate.Covers[chapter.Id] = RelinkConfidence.FileName;
            }

            return;
        }

        if (!parsed.IsVolume)
        {
            return;
        }

        var start = parsed.Volume!.Value;
        var end = parsed.VolumeEnd ?? start;
        var markers = VolumeChapterScanner.ScanCbz(candidate.AbsolutePath).ToHashSet();
        foreach (var chapter in chapters)
        {
            if (chapter.Number is { } number && markers.Contains(number))
            {
                candidate.Covers[chapter.Id] = RelinkConfidence.PageMarkers;
            }
            else if (chapter.Volume is { } volume && volume >= start && volume <= end)
            {
                candidate.Covers[chapter.Id] = RelinkConfidence.VolumeRange;
            }
            else if (markers.Count == 0 && estimator?.Invoke(chapter) is { } guessed && guessed >= start && guessed <= end)
            {
                candidate.Covers[chapter.Id] = RelinkConfidence.Estimated;
            }
        }
    }

    /// <summary>
    /// Mirrors <c>CbzLinkService.EstimateCompletedVolumeLinksAsync</c>: when a finished series has
    /// a volume file for every volume the provider counts, each chapter is provably somewhere in
    /// them, and position in the run picks which. Null when the premise does not hold.
    /// </summary>
    private static Func<Chapter, int?>? CompletedSeriesEstimator(Series series, List<Candidate> candidates)
    {
        if (series.Status is not (SeriesStatus.Completed or SeriesStatus.Cancelled) ||
            series.TotalVolumes is not > 0 || series.TotalChapters is not > 0)
        {
            return null;
        }

        var covered = candidates
            .Where(c => c.Parsed.IsVolume)
            .SelectMany(c => Enumerable.Range(c.Parsed.Volume!.Value, (c.Parsed.VolumeEnd ?? c.Parsed.Volume!.Value) - c.Parsed.Volume!.Value + 1))
            .ToHashSet();
        var totalVolumes = series.TotalVolumes.Value;
        if (Enumerable.Range(1, totalVolumes).Any(v => !covered.Contains(v)))
        {
            return null;
        }

        var chaptersPerVolume = (decimal)series.TotalChapters.Value / totalVolumes;
        return chapter => chapter.Number is > 0
            ? Math.Clamp((int)Math.Ceiling(chapter.Number.Value / chaptersPerVolume), 1, totalVolumes)
            : null;
    }

    private static RelinkPlan ToPlan(Built built)
    {
        var chapterById = built.Chapters.ToDictionary(c => c.Id);
        var assignedChapterIds = built.Assignment.Where(a => a.Value is not null).Select(a => a.Key).ToHashSet();
        var files = new List<RelinkPlanFile>();
        var moved = 0;

        foreach (var candidate in built.Candidates)
        {
            var after = built.Assignment
                .Where(a => ReferenceEquals(a.Value, candidate))
                .Select(a => chapterById[a.Key])
                .ToList();
            var before = candidate.Record is { } record
                ? built.Chapters.Where(c => c.ChapterFileId == record.Id).ToList()
                : [];
            var afterIds = after.Select(c => c.Id).ToHashSet();
            var beforeIds = before.Select(c => c.Id).ToHashSet();
            var gains = after.Where(c => !beforeIds.Contains(c.Id)).ToList();
            var loses = before.Where(c => !afterIds.Contains(c.Id)).ToList();
            moved += gains.Count;

            // Superseded means safe to drop: nothing left on it, and each chapter it could have
            // held is on some other file. A file whose chapters are unknown to Maki (newer than
            // the chapter list, say) covers nothing and is left alone for a manual link.
            var superseded = candidate.Parsed.IsRecognized
                && after.Count == 0
                && candidate.Covers.Count > 0
                && candidate.Covers.Keys.All(assignedChapterIds.Contains);

            RelinkConfidence? confidence = after.Count == 0
                ? null
                : after.Select(c => candidate.Covers[c.Id]).MinBy(Rank);

            files.Add(new RelinkPlanFile(
                candidate.RelativePath,
                Path.GetFileName(candidate.RelativePath),
                candidate.Size,
                ParsedLabel(candidate.Parsed),
                candidate.Parsed.IsVolume,
                candidate.Parsed.IsRecognized,
                confidence is { } c ? char.ToLowerInvariant(c.ToString()[0]) + c.ToString()[1..] : null,
                Labels(after),
                Labels(gains),
                Labels(loses),
                superseded));
        }

        var supersededFiles = files.Where(f => f.Superseded).ToList();
        return new RelinkPlan(
            built.Series.Id,
            files
                .OrderBy(f => f.Superseded ? 1 : 0)
                .ThenBy(f => f.IsVolume ? 0 : 1)
                .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            moved,
            supersededFiles.Count,
            supersededFiles.Sum(f => f.Size),
            files.Count(f => !f.Recognized));
    }

    private static List<string> Labels(IEnumerable<Chapter> chapters) => chapters
        .OrderBy(c => c.Number ?? decimal.MaxValue)
        .Select(c => c.Number?.ToString("0.###", CultureInfo.InvariantCulture) ?? c.Title ?? "?")
        .ToList();

    private static string? ParsedLabel(ParsedReleaseFile parsed)
    {
        if (parsed.IsChapter)
        {
            return $"Ch.{parsed.Number!.Value.ToString("0.###", CultureInfo.InvariantCulture)}";
        }

        if (!parsed.IsVolume)
        {
            return null;
        }

        return parsed.VolumeEnd is { } end && end != parsed.Volume
            ? $"Vol.{parsed.Volume}-{end}"
            : $"Vol.{parsed.Volume}";
    }
}
