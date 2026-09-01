using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <param name="ChapterFileId">The <see cref="ChapterFile"/> row whose RelativePath this moves.</param>
/// <param name="From">Path relative to the root folder, as stored today.</param>
/// <param name="To">Path relative to the root folder, under the current formats.</param>
public record SeriesRenameFile(int ChapterFileId, string From, string To);

/// <param name="Conflicts">
/// Target names two or more chapters both want. A format with no <c>{Chapter Language}</c> in it
/// does this to a series that has the same chapter in two languages, and carrying on would
/// overwrite one with the other.
/// </param>
public record SeriesRenamePlan(
    int SeriesId,
    string Title,
    string FolderFrom,
    string FolderTo,
    IReadOnlyList<SeriesRenameFile> Files,
    IReadOnlyList<string> Conflicts)
{
    public bool FolderChanged => !string.Equals(FolderFrom, FolderTo, StringComparison.Ordinal);

    public bool HasChanges => FolderChanged || Files.Count > 0;
}

public record SeriesRenameResult(
    SeriesRenamePlan? Plan,
    bool Applied,
    string? Error,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Applies the configured naming formats to a series that is already on disk. Nothing else does
/// this: changing a format never moves a file by itself, because a format is edited far more often
/// than anyone wants their library rewritten, and a rename that runs unattended has no one to read
/// its warnings.
///
/// <para>
/// Shared by the single-series and bulk endpoints so the guards can't diverge. The guards are the
/// same ones <c>SeriesController.Move</c> uses, for the same reasons: an in-flight download writes
/// into the old folder halfway through, and an existing destination folder means two series would
/// end up sharing one.
/// </para>
/// </summary>
public class SeriesRenameService(
    MakiDbContext db,
    NamingService naming,
    KavitaScanService kavitaScans,
    ILogger<SeriesRenameService> logger)
{
    /// <summary>Suffix for the two-step move a case-only rename needs on Windows.</summary>
    private const string TempSuffix = ".maki-rename";

    public async Task<SeriesRenamePlan?> PlanAsync(int seriesId, CancellationToken ct)
    {
        var series = await db.Series.Include(s => s.RootFolder)
            .FirstOrDefaultAsync(s => s.Id == seriesId, ct);
        return series is null ? null : await PlanAsync(series, ct);
    }

    public async Task<SeriesRenamePlan> PlanAsync(Series series, CancellationToken ct)
    {
        var folderTo = await naming.BuildSeriesFolderNameAsync(series, ct);

        // Only chapters carry enough to name a file. A ChapterFile nothing points at (an adopted
        // archive that never matched a chapter) is left exactly where it is.
        var chapters = await db.Chapters
            .Include(c => c.ChapterFile)
            .Where(c => c.SeriesId == series.Id && c.ChapterFileId != null)
            .ToListAsync(ct);

        var files = new List<SeriesRenameFile>();
        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var conflicts = new List<string>();

        foreach (var chapter in chapters.OrderBy(c => c.Volume).ThenBy(c => c.Number))
        {
            if (chapter.ChapterFile is not { } file)
            {
                continue;
            }

            var to = Path.Combine(folderTo, await naming.BuildChapterFileNameAsync(series, chapter, ct));

            if (targets.TryGetValue(to, out var claimedBy))
            {
                conflicts.Add($"{Path.GetFileName(to)} — wanted by both {claimedBy} and {file.RelativePath}");
                continue;
            }

            targets[to] = file.RelativePath;

            if (!string.Equals(file.RelativePath, to, StringComparison.Ordinal))
            {
                files.Add(new SeriesRenameFile(file.Id, file.RelativePath, to));
            }
        }

        return new SeriesRenamePlan(series.Id, series.Title, series.FolderName, folderTo, files, conflicts);
    }

    public async Task<SeriesRenameResult> RenameAsync(int seriesId, CancellationToken ct)
    {
        var series = await db.Series.Include(s => s.RootFolder)
            .FirstOrDefaultAsync(s => s.Id == seriesId, ct);
        if (series is null)
        {
            return new SeriesRenameResult(null, false, "Series not found", []);
        }

        if (series.RootFolder is null)
        {
            return new SeriesRenameResult(null, false, "Series has no root folder", []);
        }

        var plan = await PlanAsync(series, ct);

        if (plan.Conflicts.Count > 0)
        {
            return new SeriesRenameResult(plan, false,
                "The current chapter format gives two chapters the same file name", plan.Conflicts);
        }

        if (!plan.HasChanges)
        {
            return new SeriesRenameResult(plan, true, null, []);
        }

        var active = await db.DownloadQueue.AnyAsync(q => q.SeriesId == series.Id &&
            q.Status != QueueStatus.Completed && q.Status != QueueStatus.Failed &&
            q.Status != QueueStatus.Cancelled, ct);
        if (active)
        {
            return new SeriesRenameResult(plan, false,
                "Series has an active download — wait for it to finish before renaming", []);
        }

        var root = series.RootFolder.Path;
        var oldFolder = Path.Combine(root, plan.FolderFrom);
        var newFolder = Path.Combine(root, plan.FolderTo);
        var warnings = new List<string>();

        if (plan.FolderChanged && Directory.Exists(oldFolder))
        {
            if (Directory.Exists(newFolder) && !SamePathIgnoringCase(oldFolder, newFolder))
            {
                return new SeriesRenameResult(plan, false,
                    $"Destination folder already exists: {newFolder}", []);
            }

            try
            {
                MovePath(oldFolder, newFolder, Directory.Move);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not rename series folder for {Title}", series.Title);
                return new SeriesRenameResult(plan, false,
                    $"Could not rename the series folder: {ex.Message}", []);
            }
        }

        // The folder move above already carried the files, so each one is now under the new folder
        // under its old name — that, not the stored RelativePath, is where it actually is.
        var renamed = new List<SeriesRenameFile>();
        foreach (var file in plan.Files)
        {
            var from = Path.Combine(root, plan.FolderTo, Path.GetFileName(file.From));
            var to = Path.Combine(root, file.To);

            if (string.Equals(from, to, StringComparison.Ordinal))
            {
                // Only the folder changed; the folder move already put the file where it belongs
                // and the row just has to catch up.
                renamed.Add(file);
                continue;
            }

            if (!System.IO.File.Exists(from))
            {
                // Nothing on disk to move, but the row still has to follow the folder rename or it
                // points at a path that no longer exists.
                renamed.Add(file);
                warnings.Add($"{file.From} was missing from disk; its entry was updated anyway");
                continue;
            }

            if (System.IO.File.Exists(to) && !SamePathIgnoringCase(from, to))
            {
                warnings.Add($"Skipped {Path.GetFileName(file.From)}: {Path.GetFileName(file.To)} already exists");
                continue;
            }

            try
            {
                MovePath(from, to, (s, d) => System.IO.File.Move(s, d));
                renamed.Add(file);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not rename {From} for {Title}", file.From, series.Title);
                warnings.Add($"Could not rename {Path.GetFileName(file.From)}: {ex.Message}");
            }
        }

        // One save for the whole series: a half-written set of paths is far worse to recover from
        // than a rename that failed outright.
        series.FolderName = plan.FolderTo;
        if (renamed.Count > 0)
        {
            var byId = renamed.ToDictionary(f => f.ChapterFileId, f => f.To);
            var ids = byId.Keys.ToList();
            var rows = await db.ChapterFiles.Where(f => ids.Contains(f.Id)).ToListAsync(ct);
            foreach (var row in rows)
            {
                row.RelativePath = byId[row.Id];
            }
        }

        await db.SaveChangesAsync(ct);

        // ReaderArchiveCache needs no invalidation: it is keyed by ChapterFile id and validated
        // against the file's size, and a rename changes neither.
        kavitaScans.QueueScan(oldFolder, series.Id);
        kavitaScans.QueueScan(newFolder, series.Id);

        return new SeriesRenameResult(plan, true, null, warnings);
    }

    public async Task<IReadOnlyList<SeriesRenameResult>> RenameManyAsync(
        IEnumerable<int> seriesIds, CancellationToken ct)
    {
        var results = new List<SeriesRenameResult>();
        foreach (var id in seriesIds.Distinct())
        {
            results.Add(await RenameAsync(id, ct));
        }

        return results;
    }

    /// <summary>
    /// Renaming <c>Berserk</c> to <c>berserk</c> is a real change that Windows reports as "already
    /// exists" and refuses as a same-path move, so it goes via a temporary name.
    /// </summary>
    private static void MovePath(string from, string to, Action<string, string> move)
    {
        if (!SamePathIgnoringCase(from, to))
        {
            move(from, to);
            return;
        }

        var staging = to + TempSuffix;
        move(from, staging);
        move(staging, to);
    }

    private static bool SamePathIgnoringCase(string a, string b) =>
        !string.Equals(a, b, StringComparison.Ordinal) &&
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
