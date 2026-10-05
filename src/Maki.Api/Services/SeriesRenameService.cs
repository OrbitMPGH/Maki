using System.Security.Cryptography;
using System.Text;
using Maki.Api.Localization;
using Maki.Core.Entities;
using Maki.Core.Reading;
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

    /// <summary>
    /// Identifies what this plan would do, so a confirm can be refused when the formats or the
    /// metadata changed after the preview the user actually read.
    /// </summary>
    public string Fingerprint
    {
        get
        {
            var text = new StringBuilder(FolderTo);
            foreach (var file in Files.OrderBy(f => f.ChapterFileId))
            {
                text.Append('\n').Append(file.ChapterFileId).Append('\t').Append(file.To);
            }

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
        }
    }
}

public record SeriesRenameResult(
    SeriesRenamePlan? Plan,
    bool Applied,
    string? Error,
    IReadOnlyList<string> Warnings,
    string? ErrorCode = null);

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
    ILocalizer localizer,
    ILogger<SeriesRenameService> logger)
{
    /// <summary>Suffix for the two-step move a case-only rename needs on Windows.</summary>
    private const string TempSuffix = ".maki-rename";

    /// <summary>
    /// Name a file is staged under while it steps aside for a swap. Keeps the extension at the
    /// end: a file that never makes it back to a real name (<see cref="StayPut"/>) stays under
    /// this name, and Health/rescan only recognize a comic archive by its trailing extension.
    /// </summary>
    internal static string StagedName(string path)
    {
        var ext = Path.GetExtension(path);
        var stem = path[..^ext.Length];
        return stem + TempSuffix + "-" + Guid.NewGuid().ToString("N")[..8] + ext;
    }

    /// <summary>Paths compare the way the host's filesystem does.</summary>
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// What one rename is allowed to touch.
    /// </summary>
    /// <param name="RenameFolder">
    /// False pins the folder to the series' existing <see cref="Series.FolderName"/>, so the plan
    /// carries no folder move at all. Renaming the folder rewrites every path in the series and is
    /// visible to every other tool pointed at the library, so it stays something a person asked
    /// for rather than something an import does on their behalf.
    /// </param>
    /// <param name="FileIds">
    /// Null considers every chapter file; otherwise only these, for a caller that has just added
    /// files and wants those named without rewriting the rest of the series.
    /// </param>
    private sealed record RenameScope(bool RenameFolder, IReadOnlySet<int>? FileIds)
    {
        /// <summary>The folder and every chapter file: what "rename this series" means.</summary>
        public static readonly RenameScope Everything = new(true, null);
    }

    public async Task<SeriesRenamePlan?> PlanAsync(int seriesId, CancellationToken ct)
    {
        var series = await db.Series.Include(s => s.RootFolder)
            .FirstOrDefaultAsync(s => s.Id == seriesId, ct);
        return series is null ? null : await PlanAsync(series, ct);
    }

    public Task<SeriesRenamePlan> PlanAsync(Series series, CancellationToken ct) =>
        PlanAsync(series, RenameScope.Everything, ct);

    private async Task<SeriesRenamePlan> PlanAsync(Series series, RenameScope scope, CancellationToken ct)
    {
        var folderTo = scope.RenameFolder
            ? await naming.BuildSeriesFolderNameAsync(series, ct)
            : series.FolderName;

        // Only chapters carry enough to name a file. A ChapterFile nothing points at (an adopted
        // archive that never matched a chapter) is left exactly where it is.
        var chapters = await db.Chapters
            .Include(c => c.ChapterFile)
            .Where(c => c.SeriesId == series.Id && c.ChapterFileId != null)
            .ToListAsync(ct);

        var files = new List<SeriesRenameFile>();
        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var conflicts = new List<string>();

        // A single ChapterFile can back more than one Chapter row (a combined-volume archive), and
        // it gets one name for the whole span. Naming it after its first chapter alone hands it the
        // name that chapter's own file wants — on a case-sensitive filesystem that lands a second
        // file beside the first rather than being refused.
        var ordered = chapters.OrderBy(c => c.Volume).ThenBy(c => c.Number).ToList();
        foreach (var span in ordered
                     .Where(c => c.ChapterFile is not null)
                     .GroupBy(c => c.ChapterFileId!.Value))
        {
            var chapter = span.First();
            var file = chapter.ChapterFile!;

            if (scope.FileIds is { } wanted && !wanted.Contains(file.Id))
            {
                continue;
            }

            var through = span.Last();
            var existingExtension = Path.GetExtension(file.RelativePath);
            var to = Path.Combine(folderTo, ComicFile.IsPdf(existingExtension)
                ? await naming.BuildChapterFileNameAsync(
                    series, chapter, through, CoversWholeVolumes(ordered, span), existingExtension, ct)
                : await naming.BuildChapterFileNameAsync(
                    series, chapter, through, CoversWholeVolumes(ordered, span), ct));

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

    /// <summary>
    /// Whether a file's chapters are every chapter the series has in the volumes they cover — the
    /// only case where "Vol.1" names the file honestly. Half a volume keeps its chapter range, so
    /// two files splitting one volume don't both ask for the same name.
    /// </summary>
    private static bool CoversWholeVolumes(IReadOnlyCollection<Chapter> all, IEnumerable<Chapter> span)
    {
        var volumes = span.Select(c => c.Volume).ToList();
        if (volumes.Count < 2 || volumes.Any(v => v is null))
        {
            return false;
        }

        var fileId = span.First().ChapterFileId;
        return all
            .Where(c => c.Volume >= volumes.Min() && c.Volume <= volumes.Max())
            .All(c => c.ChapterFileId == fileId);
    }

    public Task<SeriesRenameResult> RenameAsync(int seriesId, CancellationToken ct) =>
        RenameAsync(seriesId, expectedFingerprint: null, ct);

    /// <param name="expectedFingerprint">
    /// The <see cref="SeriesRenamePlan.Fingerprint"/> of the preview being confirmed. A plan that no
    /// longer matches it is refused rather than applied unseen.
    /// </param>
    public async Task<SeriesRenameResult> RenameAsync(
        int seriesId, string? expectedFingerprint, CancellationToken ct)
    {
        using var seriesLock = await SeriesLocks.SeriesAsync(seriesId, ct);
        return await RenameAsync(seriesId, RenameScope.Everything, expectedFingerprint, ct);
    }

    /// <summary>
    /// Applies the chapter format to a specific set of <see cref="ChapterFile"/> rows and nothing
    /// else, for a caller that has just put those files in the library.
    /// <para>
    /// The series folder is deliberately left where it is. An import is not the user asking for
    /// their library to be reorganised, and the folder format's default carries a year that
    /// folders created before it did not, so renaming here would move a whole series' worth of
    /// files off the back of one grabbed release.
    /// </para>
    /// </summary>
    public Task<SeriesRenameResult> RenameFilesAsync(
        int seriesId, IReadOnlyCollection<int> chapterFileIds, CancellationToken ct) =>
        chapterFileIds.Count == 0
            ? Task.FromResult(new SeriesRenameResult(null, true, null, []))
            : RenameAsync(seriesId, new RenameScope(false, chapterFileIds.ToHashSet()), null, ct);

    private async Task<SeriesRenameResult> RenameAsync(
        int seriesId, RenameScope scope, string? expectedFingerprint, CancellationToken ct)
    {
        var series = await db.Series.Include(s => s.RootFolder)
            .FirstOrDefaultAsync(s => s.Id == seriesId, ct);
        if (series is null)
        {
            return new SeriesRenameResult(null, false, localizer.Get("error.seriesRename.notFound"), []);
        }

        if (series.RootFolder is null)
        {
            return new SeriesRenameResult(null, false, localizer.Get("error.series.noRootFolder"), []);
        }

        var plan = await PlanAsync(series, scope, ct);

        if (expectedFingerprint is not null &&
            !string.Equals(expectedFingerprint, plan.Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            const string planChanged = "error.seriesRename.planChanged";
            return new SeriesRenameResult(plan, false, localizer.Get(planChanged), [], planChanged);
        }

        if (plan.Conflicts.Count > 0)
        {
            return new SeriesRenameResult(plan, false,
                localizer.Get("error.seriesRename.formatCollision"), plan.Conflicts);
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
                localizer.Get("error.seriesRename.activeDownload"), []);
        }

        var root = series.RootFolder.Path;
        var oldFolder = Path.Combine(root, plan.FolderFrom);
        var newFolder = Path.Combine(root, plan.FolderTo);
        var warnings = new List<string>();
        var occupied = new OccupiedNames();
        var moves = new List<Move>();

        if (plan.FolderChanged && Directory.Exists(oldFolder))
        {
            if (occupied.Taken(newFolder) && !SamePathIgnoringCase(oldFolder, newFolder))
            {
                return new SeriesRenameResult(plan, false,
                    localizer.Get("error.series.destinationExists", new { folder = newFolder }), []);
            }

            try
            {
                MovePath(oldFolder, newFolder, Directory.Move);
                moves.Add(new Move(oldFolder, newFolder, IsFolder: true));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not rename series folder for {Title}", series.Title);
                return new SeriesRenameResult(plan, false,
                    $"Could not rename the series folder: {ex.Message}", []);
            }
        }

        // The folder move above already carried the files, so each one is now under the new folder
        // at its old name. That, not the stored RelativePath, is where it actually is.
        var sources = plan.Files.ToDictionary(
            f => f.ChapterFileId, f => SourceAfterFolderMove(root, plan, f.From));

        // A file whose current name another planned file wants (a swap, or a chain) steps aside to a
        // temporary name first, so in-plan sources are vacated before anything needs their names.
        foreach (var file in plan.Files)
        {
            var from = sources[file.ChapterFileId];
            var wanted = plan.Files.Any(other => other.ChapterFileId != file.ChapterFileId &&
                string.Equals(Path.Combine(root, other.To), from, StringComparison.OrdinalIgnoreCase));
            if (!wanted || !System.IO.File.Exists(from))
            {
                continue;
            }

            var staged = StagedName(from);
            try
            {
                System.IO.File.Move(from, staged);
                occupied.Moved(from, staged);
                moves.Add(new Move(from, staged, IsFolder: false));
                sources[file.ChapterFileId] = staged;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not stage {From} for {Title}", file.From, series.Title);
            }
        }

        var renamed = new List<SeriesRenameFile>();
        foreach (var file in plan.Files)
        {
            var from = sources[file.ChapterFileId];
            var original = SourceAfterFolderMove(root, plan, file.From);
            var to = Path.Combine(root, file.To);

            if (string.Equals(from, to, StringComparison.Ordinal))
            {
                // Only the folder changed; the folder move already put the file where it belongs
                // and the row just has to catch up.
                renamed.Add(file);
                continue;
            }

            if (occupied.Taken(to) && !SamePathIgnoringCase(from, to))
            {
                // Ahead of the missing-from-disk case below: repointing a row at a name another
                // file already answers to leaves two rows describing one archive.
                warnings.Add(localizer.Get("error.seriesRename.fileSkippedExists", new
                {
                    from = Path.GetFileName(file.From), to = Path.GetFileName(file.To)
                }));
                renamed.Add(file with { To = StayPut(root, from, original, occupied, moves) });
                continue;
            }

            if (!System.IO.File.Exists(from))
            {
                // Nothing on disk to move, but the row still has to follow the folder rename or it
                // points at a path that no longer exists.
                renamed.Add(file);
                warnings.Add(localizer.Get("error.seriesRename.fileMissingUpdated", new { file = file.From }));
                continue;
            }

            try
            {
                MovePath(from, to, (s, d) => System.IO.File.Move(s, d));
                occupied.Moved(from, to);
                moves.Add(new Move(from, to, IsFolder: false));
                renamed.Add(file);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not rename {From} for {Title}", file.From, series.Title);
                warnings.Add($"Could not rename {Path.GetFileName(file.From)}: {ex.Message}");
                renamed.Add(file with { To = StayPut(root, from, original, occupied, moves) });
            }
        }

        // Once anything has moved, a cancelled request must not leave the rows describing the old
        // layout. One save for the whole series: a half-written set of paths is far worse to
        // recover from than a rename that failed outright.
        var saveToken = moves.Count > 0 ? CancellationToken.None : ct;
        series.FolderName = plan.FolderTo;
        var byId = renamed
            .Where(f => !string.Equals(f.From, f.To, StringComparison.Ordinal))
            .ToDictionary(f => f.ChapterFileId, f => f.To);
        if (byId.Count > 0)
        {
            var ids = byId.Keys.ToList();
            var rows = await db.ChapterFiles.Where(f => ids.Contains(f.Id)).ToListAsync(saveToken);
            foreach (var row in rows)
            {
                row.RelativePath = byId[row.Id];
            }
        }

        try
        {
            await db.SaveChangesAsync(saveToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Saving the rename of {Title} failed; reversing {Count} moves",
                series.Title, moves.Count);
            UndoMoves(moves, series.Title);
            throw;
        }

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
    /// Where a chapter file actually sits once the folder move has run: the same path it had, with
    /// the series folder swapped for the new one.
    /// <para>
    /// The tail is kept whole rather than reduced to the file name. A library adopted from disk can
    /// have chapters nested inside the series folder (<c>Berserk/Volume 01/ch1.cbz</c>) because the
    /// importer enumerates recursively, and taking only the file name there names a path that has
    /// never existed — the move is then skipped as "missing" while the row is repointed at it,
    /// which loses the file as far as the reader, OPDS and the health scan are concerned.
    /// </para>
    /// <para>
    /// A stored path that is not under the series folder at all (data written before a move, a
    /// hand-edited row) is left where it says it is: the folder rename did not carry it either.
    /// </para>
    /// </summary>
    private static string SourceAfterFolderMove(string root, SeriesRenamePlan plan, string storedPath)
    {
        var tail = TailUnder(plan.FolderFrom, storedPath);
        return tail is null
            ? Path.Combine(root, storedPath)
            : Path.Combine(root, plan.FolderTo, tail);
    }

    /// <summary>The part of <paramref name="path"/> below <paramref name="folder"/>, or null when it isn't under it.</summary>
    private static string? TailUnder(string folder, string path)
    {
        if (folder.Length == 0 || path.Length <= folder.Length + 1 ||
            !path.StartsWith(folder, PathComparison))
        {
            return null;
        }

        return path[folder.Length] is '/' or '\\' ? path[(folder.Length + 1)..] : null;
    }

    /// <summary>
    /// Renaming <c>Berserk</c> to <c>berserk</c> is a real change that Windows reports as "already
    /// exists" and refuses as a same-path move, so it goes via a temporary name.
    /// </summary>
    internal static void MovePath(string from, string to, Action<string, string> move)
    {
        if (!SamePathIgnoringCase(from, to))
        {
            move(from, to);
            return;
        }

        var staging = to + TempSuffix;
        move(from, staging);
        try
        {
            move(staging, to);
        }
        catch
        {
            move(staging, from);
            throw;
        }
    }

    private sealed record Move(string From, string To, bool IsFolder);

    /// <summary>
    /// Where a file that did not reach its target ends up, relative to the root. A file staged
    /// aside for a swap goes back to its own name when that is still free; otherwise it stays
    /// where it is, and the row records that rather than a path nothing answers to.
    /// </summary>
    private string StayPut(
        string root, string current, string original, OccupiedNames occupied, List<Move> moves)
    {
        if (!string.Equals(current, original, StringComparison.Ordinal) &&
            System.IO.File.Exists(current) && !occupied.Taken(original))
        {
            try
            {
                System.IO.File.Move(current, original);
                occupied.Moved(current, original);
                moves.Add(new Move(current, original, IsFolder: false));
                current = original;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not move staged {Staged} back to {Original}", current, original);
            }
        }

        return Path.GetRelativePath(root, current);
    }

    private void UndoMoves(List<Move> moves, string title)
    {
        for (var i = moves.Count - 1; i >= 0; i--)
        {
            var move = moves[i];
            try
            {
                if (move.IsFolder)
                {
                    MovePath(move.To, move.From, Directory.Move);
                }
                else
                {
                    MovePath(move.To, move.From, (s, d) => System.IO.File.Move(s, d));
                }

                logger.LogInformation("Reversed rename of {To} to {From} for {Title}", move.To, move.From, title);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not reverse rename of {To} to {From} for {Title}",
                    move.To, move.From, title);
            }
        }
    }

    private static bool SamePathIgnoringCase(string a, string b) =>
        !string.Equals(a, b, StringComparison.Ordinal) &&
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Which names a directory already answers to, whatever their spelling.
    /// <para>
    /// <c>File.Exists</c> answers the filesystem's own question, and a case-sensitive one calls a
    /// name that differs from an existing file only in case free. The move then lands a second file
    /// beside the first — two entries that every case-insensitive lookup over the library (the
    /// files endpoint, the rescan's known-paths set, Kavita) reads as one, and a series whose title
    /// has been re-cased by a metadata refresh produces exactly that pair on every import.
    /// </para>
    /// <para>
    /// Listings are cached per directory and kept in step with the moves, so a rename of a large
    /// series costs one listing rather than one per file.
    /// </para>
    /// </summary>
    private sealed class OccupiedNames
    {
        private readonly Dictionary<string, HashSet<string>> _byDirectory =
            new(StringComparer.OrdinalIgnoreCase);

        public bool Taken(string path) => Names(path).Contains(Path.GetFileName(path));

        public void Moved(string from, string to)
        {
            Names(from).Remove(Path.GetFileName(from));
            Names(to).Add(Path.GetFileName(to));
        }

        private HashSet<string> Names(string path)
        {
            var directory = Path.GetDirectoryName(path) ?? string.Empty;
            if (_byDirectory.TryGetValue(directory, out var names))
            {
                return names;
            }

            names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    names.Add(Path.GetFileName(entry));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable directory: nothing known to be taken, and the move that follows
                // reports the real error per file rather than failing the whole rename here.
            }

            _byDirectory[directory] = names;
            return names;
        }
    }
}
