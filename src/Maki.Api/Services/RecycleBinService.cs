using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Paths;
using Maki.Core.Security;
using Maki.Core.Storage;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Where files the user deleted wait: <c>&lt;root&gt;/.maki-trash/bin/&lt;SeriesId&gt;/</c>, beside the
/// upgrade trash and purged by the same housekeeping pass. Being under the root folder keeps a move
/// on the volume the file is already on, so binning is a rename and a hardlinked file stays one.
/// </summary>
public static class RecycleBin
{
    public const string FolderName = "bin";
    public const int DefaultRetentionDays = 14;

    /// <summary>How old an entry must be before a purge may take it, whatever the retention.</summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromMinutes(5);

    public static string Folder(string rootPath) => Path.Combine(rootPath, UpgradeTrash.FolderName, FolderName);

    /// <summary>The longest file name most filesystems take, in UTF-8 bytes (and so in UTF-16 units too).</summary>
    private const int MaxNameBytes = 255;

    /// <summary>
    /// <c>.maki-trash/bin/&lt;seriesId&gt;/&lt;tag&gt;-&lt;name&gt;</c>. The tag keeps two deletions of one name
    /// apart; the name is shortened to fit beside it, since the entry keeps the real one for restore.
    /// </summary>
    public static string NewRelativePath(int seriesId, string name)
    {
        var tag = Guid.NewGuid().ToString("N")[..12];
        return $"{UpgradeTrash.FolderName}/{FolderName}/{seriesId.ToString(CultureInfo.InvariantCulture)}/{tag}-{Fit(name, MaxNameBytes - tag.Length - 1)}";
    }

    /// <summary>Shortens the stem of <paramref name="name"/> by whole characters until it fits <paramref name="maxBytes"/> of UTF-8. The extension stays.</summary>
    public static string Fit(string name, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(name) <= maxBytes)
        {
            return name;
        }

        var extension = Path.GetExtension(name);
        if (Encoding.UTF8.GetByteCount(extension) > maxBytes / 2)
        {
            extension = string.Empty;
        }

        var budget = maxBytes - Encoding.UTF8.GetByteCount(extension);
        var stem = new StringBuilder();
        var elements = StringInfo.GetTextElementEnumerator(name[..^extension.Length]);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            budget -= Encoding.UTF8.GetByteCount(element);
            if (budget < 0)
            {
                break;
            }

            stem.Append(element);
        }

        return stem + extension;
    }

    private static readonly Regex Tag = new("^([0-9a-f]{12})-", RegexOptions.CultureInvariant);

    /// <summary>True when <paramref name="fileName"/> carries the tag of a bin entry that still exists.</summary>
    public static async Task<bool> TaggedAsync(MakiDbContext db, string fileName, CancellationToken ct)
    {
        if (Tag.Match(fileName) is not { Success: true } match)
        {
            return false;
        }

        var needle = $"/{match.Groups[1].Value}-";
        return await db.RecycleBin.IgnoreQueryFilters().AnyAsync(e => e.BinPath.Contains(needle), ct);
    }

    public static async Task<int> RetentionDaysAsync(IAppSettings settings, CancellationToken ct) =>
        int.TryParse(await settings.GetAsync(SettingKeys.RecycleBinRetentionDays, ct), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var days)
            ? Math.Clamp(days, 0, 365)
            : DefaultRetentionDays;
}

/// <summary>The one rename the bin uses, virtual so tests can stand in a move that fails or crosses volumes.</summary>
public class RecycleBinMover
{
    public virtual MoveOutcome Move(string source, string target) => SameVolumeMove.Move(source, target);
}

/// <summary>
/// Puts deleted files in the bin and takes them back out.
/// <para>
/// The entry is always saved before its file moves, and removed only after the file has left the
/// bin, so the bin never holds a file nothing records. Every caller follows the same order: record,
/// save, move, then settle the rows of whatever did not move. A file that cannot be moved stays
/// where it was, never deleted in its place.
/// </para>
/// </summary>
public class RecycleBinService(
    MakiDbContext db,
    IAppSettings settings,
    ICurrentUser currentUser,
    RecycleBinMover mover,
    TimeProvider time,
    ILogger<RecycleBinService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record ChapterSnapshot(int Id, decimal? Number, int? Volume, string Language);

    public enum RestoreStatus
    {
        Linked,
        Unlinked,
        NotFound,
        FileMissing,
        TargetExists,
        TargetInvalid,
        CrossVolume,
        Failed
    }

    public sealed record RestoreResult(RestoreStatus Status, int ChaptersLinked = 0);

    /// <summary>
    /// True unless a folder holding one of <paramref name="absolutePaths"/> is on another volume than
    /// the bin. Probes with a throwaway file per folder rather than guessing from drive letters or
    /// mount tables, which are wrong under Docker bind mounts. A probe that fails for any other reason
    /// says nothing, and the real move will then find out on its own.
    /// </summary>
    public bool SameVolume(string rootPath, IEnumerable<string> absolutePaths)
    {
        var bin = RecycleBin.Folder(rootPath);
        foreach (var directory in absolutePaths.Select(Path.GetDirectoryName).OfType<string>()
                     .Distinct(LibraryPaths.FolderComparer))
        {
            var name = $".maki-bin-probe-{Guid.NewGuid():N}";
            var probe = Path.Combine(directory, name);
            var landed = Path.Combine(bin, name);
            try
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                UpgradeTrash.EnsureDirectory(rootPath, bin);
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write))
                {
                }

                if (mover.Move(probe, landed).Result == MoveResult.CrossVolume)
                {
                    logger.LogWarning("{Directory} is on another volume than the recycle bin in {Root}", directory, rootPath);
                    return false;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Could not probe {Directory} for the recycle bin", directory);
            }
            finally
            {
                TryDelete(probe);
                TryDelete(landed);
            }
        }

        return true;
    }

    /// <summary>Adds an entry for a file about to be binned. The caller saves it, then calls <see cref="MoveInAsync"/>.</summary>
    public RecycleBinEntry Record(
        Series series, string rootPath, string relativePath, string absolutePath, ChapterFile? file,
        IEnumerable<Chapter> chapters, RecycleReason reason)
    {
        long size = file?.Size ?? 0;
        try
        {
            size = new FileInfo(absolutePath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        var entry = new RecycleBinEntry
        {
            RootFolderId = series.RootFolderId,
            RootPath = rootPath,
            RelativePath = relativePath,
            OriginalPath = absolutePath,
            BinPath = RecycleBin.NewRelativePath(series.Id, Path.GetFileName(absolutePath)),
            SeriesId = series.Id,
            SeriesTitle = series.Title,
            ChapterFileId = file?.Id,
            FileJson = file is null ? null : JsonSerializer.Serialize(file, Json),
            ChaptersJson = JsonSerializer.Serialize(
                chapters.Select(c => new ChapterSnapshot(c.Id, c.Number, c.Volume, c.Language)).ToList(), Json),
            Size = size,
            Reason = reason,
            DeletedAtUtc = time.GetUtcNow().UtcDateTime,
            DeletedByUserId = currentUser.UserId == 0 ? null : currentUser.UserId,
            DeletedByName = currentUser.UserId == 0 ? null : currentUser.UserName
        };
        db.RecycleBin.Add(entry);
        return entry;
    }

    /// <summary>
    /// Moves a recorded file into the bin. False leaves it where it was: the caller removes the entry
    /// and keeps the file's rows. Retried, because the reader or Kavita may hold the file for a moment.
    /// </summary>
    public async Task<bool> MoveInAsync(RecycleBinEntry entry, CancellationToken ct)
    {
        if (LibraryPaths.Resolve(entry.RootPath, entry.BinPath) is not { } target)
        {
            return false;
        }

        try
        {
            UpgradeTrash.EnsureDirectory(entry.RootPath, Path.GetDirectoryName(target)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not create the recycle bin folder for {File}", entry.OriginalPath);
            return false;
        }

        var outcome = await MoveAsync(entry.OriginalPath, target, ct);
        if (outcome.Result != MoveResult.Moved)
        {
            logger.LogWarning(outcome.Error, "Could not move {File} to the recycle bin ({Result})", entry.OriginalPath, outcome.Result);
        }

        return outcome.Result == MoveResult.Moved;
    }

    /// <summary>
    /// Drops the entries of files that never reached the bin, as their own statement so an entry a
    /// purge already removed is no error and the caller's pending row changes are not lost with it.
    /// </summary>
    public async Task ForgetAsync(IReadOnlyCollection<RecycleBinEntry> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        var ids = entries.Select(e => e.Id).ToList();
        foreach (var entry in entries)
        {
            db.Entry(entry).State = EntityState.Detached;
        }

        await db.RecycleBin.IgnoreQueryFilters().Where(e => ids.Contains(e.Id)).ExecuteDeleteAsync(CancellationToken.None);
    }

    private async Task<MoveOutcome> MoveAsync(string from, string to, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var outcome = mover.Move(from, to);
            if (outcome.Racy)
            {
                logger.LogWarning("Moved {From} with a check then a rename: the filesystem has no hard links or no-replace rename", from);
            }

            // A share can report a failure for a rename that happened (an NFS retransmit).
            if (outcome.Result != MoveResult.Moved && SameVolumeMove.Landed(from, to))
            {
                return new MoveOutcome(MoveResult.Moved);
            }

            if (outcome.Result != MoveResult.Failed || attempt >= UpgradeTrash.MoveBackoff.Length || !SameVolumeMove.Occupied(from))
            {
                return outcome;
            }

            await Task.Delay(UpgradeTrash.MoveBackoff[attempt], ct);
        }
    }

    public static List<ChapterSnapshot> Chapters(RecycleBinEntry entry) =>
        JsonSerializer.Deserialize<List<ChapterSnapshot>>(entry.ChaptersJson, Json) ?? [];

    public static string? BinFile(RecycleBinEntry entry) => LibraryPaths.Resolve(entry.RootPath, entry.BinPath);

    /// <summary>
    /// Moves the file back to where it was and, when its series still exists in the same root folder,
    /// gives it back to the chapters it backed that have no file now. Refuses rather than overwrite a
    /// file that has taken its place. A file whose chapters or series are gone is still put back, as an
    /// unlinked file Health can import.
    /// </summary>
    public async Task<RestoreResult> RestoreAsync(int id, CancellationToken ct)
    {
        var entry = await db.RecycleBin.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (entry is null)
        {
            return new RestoreResult(RestoreStatus.NotFound);
        }

        // Occupied rather than File.Exists: a binned symlink whose target is gone is still a file to put back.
        if (BinFile(entry) is not { } source || !SameVolumeMove.Occupied(source))
        {
            return new RestoreResult(RestoreStatus.FileMissing);
        }

        if (LibraryPaths.ResolveNoLinks(entry.RootPath, entry.RelativePath) is not { } target)
        {
            return new RestoreResult(RestoreStatus.TargetInvalid);
        }

        using var seriesLock = await SeriesLocks.SeriesAsync(entry.SeriesId, ct);
        if (SameVolumeMove.Occupied(target))
        {
            return new RestoreResult(RestoreStatus.TargetExists);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not recreate the folder for {File}", target);
            return new RestoreResult(RestoreStatus.Failed);
        }

        var outcome = await MoveAsync(source, target, ct);
        switch (outcome.Result)
        {
            case MoveResult.TargetExists:
                return new RestoreResult(RestoreStatus.TargetExists);
            case MoveResult.CrossVolume:
                return new RestoreResult(RestoreStatus.CrossVolume);
            case MoveResult.Failed:
                logger.LogWarning(outcome.Error, "Could not restore {File} from the recycle bin", target);
                return new RestoreResult(RestoreStatus.Failed);
        }

        // The file is back where it was. Whatever happens to the rows now, it is on disk, and at
        // worst Health reports it as an unlinked file.
        var linked = 0;
        var relinked = false;
        try
        {
            (relinked, linked) = await RelinkAsync(entry, ct);
            db.RecycleBin.Remove(entry);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Restored {File} to disk but could not update its records", target);
            db.ChangeTracker.Clear();
            relinked = false;
        }

        await QueueHealthScanAsync(entry, relinked);
        return new RestoreResult(relinked ? RestoreStatus.Linked : RestoreStatus.Unlinked, linked);
    }

    private async Task<(bool Relinked, int Chapters)> RelinkAsync(RecycleBinEntry entry, CancellationToken ct)
    {
        var series = await db.Series.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == entry.SeriesId && s.RootFolderId == entry.RootFolderId, ct);
        if (series is null)
        {
            return (false, 0);
        }

        var key = LibraryPaths.ComparisonKey(entry.RelativePath);
        var rows = await db.ChapterFiles.IgnoreQueryFilters().Where(f => f.SeriesId == series.Id).ToListAsync(ct);
        // Binned but never settled (the save after the move failed): the row was never removed.
        if (rows.FirstOrDefault(f => f.Id == entry.ChapterFileId) is { } kept &&
            LibraryPaths.ComparisonKey(kept.RelativePath) == key)
        {
            return (true, 0);
        }

        if (entry.FileJson is null || JsonSerializer.Deserialize<ChapterFile>(entry.FileJson, Json) is not { } snapshot)
        {
            return (false, 0);
        }

        var chapterIds = Chapters(entry).Select(c => c.Id).ToList();
        var chapters = await db.Chapters.IgnoreQueryFilters()
            .Where(c => chapterIds.Contains(c.Id) && c.SeriesId == series.Id && c.ChapterFileId == null)
            .ToListAsync(ct);
        if (chapters.Count == 0)
        {
            return (false, 0);
        }

        var file = rows.FirstOrDefault(f => LibraryPaths.ComparisonKey(f.RelativePath) == key);
        if (file is null)
        {
            file = snapshot;
            file.Id = 0;
            file.SeriesId = series.Id;
            file.RelativePath = entry.RelativePath;
            db.ChapterFiles.Add(file);
        }

        // Linking clears FileRemovedAt (MakiDbContext.StampFileAndCompletion).
        foreach (var chapter in chapters)
        {
            chapter.ChapterFile = file;
        }

        return (true, chapters.Count);
    }

    /// <summary>
    /// A series scan rebinds the inventory row to the recreated record; a root scan is what finds an
    /// unlinked archive, which is how a file whose series is gone reaches Health's import.
    /// </summary>
    private async Task QueueHealthScanAsync(RecycleBinEntry entry, bool relinked)
    {
        try
        {
            if (!await db.RootFolders.AnyAsync(r => r.Id == entry.RootFolderId))
            {
                return;
            }

            int? seriesId = relinked ? entry.SeriesId : null;
            if (await db.HealthScans.AnyAsync(s => s.Status == "pending" && s.RootFolderId == entry.RootFolderId &&
                                                   s.SeriesId == seriesId && s.FileIdsJson == "[]"))
            {
                return;
            }

            db.HealthScans.Add(new HealthScan { RootFolderId = entry.RootFolderId, SeriesId = seriesId });
            await db.SaveChangesAsync();
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            logger.LogDebug(ex, "Could not queue a health scan after restoring {File}", entry.OriginalPath);
        }
    }

    /// <summary>Deletes the entry's file for good, then the entry. False when the file could not be deleted; the entry stays.</summary>
    public async Task<bool> DeleteAsync(RecycleBinEntry entry, CancellationToken ct)
    {
        if (!DeleteFile(entry))
        {
            return false;
        }

        db.RecycleBin.Remove(entry);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Deletes every entry the caller can see. Returns how many went and how many could not.</summary>
    public async Task<(int Deleted, int Failed)> EmptyAsync(CancellationToken ct)
    {
        var deleted = 0;
        var failed = 0;
        foreach (var entry in await db.RecycleBin.ToListAsync(ct))
        {
            if (DeleteFile(entry))
            {
                db.RecycleBin.Remove(entry);
                deleted++;
            }
            else
            {
                failed++;
            }
        }

        await db.SaveChangesAsync(CancellationToken.None);
        return (deleted, failed);
    }

    /// <summary>A bin path that resolves outside its root is never deleted, only forgotten.</summary>
    internal bool DeleteFile(RecycleBinEntry entry)
    {
        if (BinFile(entry) is not { } path)
        {
            return true;
        }

        try
        {
            File.Delete(path);
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete {File} from the recycle bin", path);
            return false;
        }
    }

    public static async Task<(long Bytes, int Files)> SizeAsync(MakiDbContext db, CancellationToken ct)
    {
        var sizes = await db.RecycleBin.IgnoreQueryFilters().AsNoTracking().Select(e => e.Size).ToListAsync(ct);
        return (sizes.Sum(), sizes.Count);
    }

    public Task<int> RetentionDaysAsync(CancellationToken ct) => RecycleBin.RetentionDaysAsync(settings, ct);

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not remove probe {Path}", path);
        }
    }
}
