using System.Globalization;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Images;
using Maki.Core.Io;
using Maki.Core.Paths;
using Maki.Core.Storage;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

public enum FileConversionStatus
{
    Converted,

    /// <summary>Looked at and left as it was; <see cref="FileConversion.Outcome"/> says why.</summary>
    Unchanged,

    /// <summary>Shares its data with a torrent client's seeding copy, so a converted copy would add to the disk, not save.</summary>
    Hardlinked,

    /// <summary>A download, rename or other writer had the file; tried again on the next pass.</summary>
    Busy,

    /// <summary>Not on disk, or could not be read or replaced right now; tried again on the next pass.</summary>
    Failed
}

public sealed record FileConversion(FileConversionStatus Status, long SavedBytes = 0, ArchiveConversionOutcome? Outcome = null);

/// <summary>What one pass works out once and reuses for every file.</summary>
public sealed class PageConversionPassCache
{
    /// <summary>Whether hardlinks hold up in each root folder, so each root is probed once.</summary>
    public Dictionary<string, bool> Hardlinks { get; } = new(StringComparer.Ordinal);

    /// <summary>Per series, the files read-file cleanup deletes within <see cref="PageConversionService.CleanupHorizonDays"/>.</summary>
    public Dictionary<int, HashSet<int>> CleanupDue { get; } = [];

    /// <summary>The pass's start stamp, recorded on each history row it writes.</summary>
    public string? PassId { get; init; }
}

/// <summary>
/// Converts the pages of one library file in place. The original goes to the upgrade trash
/// (<c>.maki-trash/&lt;SeriesId&gt;/conv-&lt;FileId&gt;-&lt;name&gt;</c>) and a
/// <see cref="PageConversionHistory"/> row records it, so an undo can put it back until the trash
/// purge removes it after <c>upgrades.trashRetentionDays</c>. The converted copy is built
/// under <c>&lt;root&gt;/.maki/tmp</c>, outside the series lock, and swapped in under it with a rename,
/// so a crash leaves the library path holding one complete archive or the other.
/// </summary>
public class PageConversionService(
    MakiDbContext db, ReaderArchiveCache archives, KavitaScanService kavitaScans, IAppSettings appSettings,
    ILogger<PageConversionService> logger)
{
    public const string TrashPrefix = "conv-";

    /// <summary>A file read-file cleanup will delete within this many days is not worth converting.</summary>
    public const int CleanupHorizonDays = 7;

    /// <summary>
    /// How long after its last page request a queued file still counts as being read. A swap mid-chapter
    /// changes the archive version, which drops the chapter's server upscales and cached pages.
    /// </summary>
    public static readonly TimeSpan ReadingQuiet = TimeSpan.FromMinutes(10);

    internal Func<string, string, bool> Hardlink { get; init; } = FileLinker.TryHardlink;

    internal Action<string, string> MoveOver { get; init; } = (from, to) => File.Move(from, to, overwrite: true);

    internal Func<string, long?> FreeSpace { get; init; } = DiskSpace.AvailableFor;

    internal Action<string> DropCache { get; init; } = PageCache.DropAfterScan;

    /// <summary>
    /// Windows refuses to rename an open file or move the folder it is in, and a conversion can take
    /// minutes, so there it reads a copy rather than holding the library file.
    /// </summary>
    internal bool CopiesSource { get; init; } = OperatingSystem.IsWindows();

    internal Func<string, string, PageConversionSettings, CancellationToken, Task<ArchiveConversionResult>> Converter { get; init; } =
        ArchivePageConverter.ConvertAsync;

    internal Func<string, PageConversionSettings, CancellationToken, Task<bool>> Probe { get; init; } =
        ArchivePageConverter.ProbeAsync;

    public Task<FileConversion> ConvertAsync(int fileId, PageConversionSettings settings, CancellationToken ct) =>
        ConvertAsync(fileId, PageConversionPlan.For(settings), new PageConversionPassCache(), ct);

    public Task<FileConversion> ConvertAsync(int fileId, PageConversionSettings settings,
        PageConversionPassCache cache, CancellationToken ct) =>
        ConvertAsync(fileId, PageConversionPlan.For(settings), cache, ct);

    /// <param name="plan">The plan the pass started with; every file of a pass is decided by the same one.</param>
    /// <param name="cache">Shared across a pass.</param>
    /// <param name="queued">
    /// For <see cref="PageConversionWorker"/>'s queue of new files rather than a pass. A file Maki downloaded
    /// from a source keeps no original, as when downloads converted before placement; an import's still goes
    /// to the trash. Only a download of one of the file's own chapters holds it up, not one anywhere in the
    /// series, and a file a reader was served a page of within <see cref="ReadingQuiet"/> waits as Busy.
    /// </param>
    public async Task<FileConversion> ConvertAsync(int fileId, PageConversionPlan plan,
        PageConversionPassCache cache, CancellationToken ct, bool queued = false)
    {
        var row = await db.ChapterFiles.IgnoreQueryFilters().AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => new
            {
                File = f,
                Root = db.Series.IgnoreQueryFilters().Where(s => s.Id == f.SeriesId).Select(s => s.RootFolder!.Path).FirstOrDefault()
            })
            .FirstOrDefaultAsync(ct);
        if (row?.Root is null || row.File.ConversionCheckedAtUtc is not null)
        {
            return new FileConversion(FileConversionStatus.Unchanged);
        }

        var file = row.File;
        var path = LibraryPaths.Resolve(row.Root, file.RelativePath);
        if (path is null || !UpgradeTrash.IsReplaceable(file.RelativePath))
        {
            await MarkCheckedAsync(file, PlanAction.HardSkipName, ct);
            return new FileConversion(FileConversionStatus.Unchanged);
        }

        if (!File.Exists(path))
        {
            return new FileConversion(FileConversionStatus.Failed);
        }

        if (HardLinks.IsShared(path))
        {
            await MarkCheckedAsync(file, PlanAction.HardSkipName, ct);
            return new FileConversion(FileConversionStatus.Hardlinked);
        }

        if (await BusyAsync(file, queued, ct) || (queued && archives.ReadWithin(path, ReadingQuiet)))
        {
            return new FileConversion(FileConversionStatus.Busy);
        }

        var keepOriginal = !queued || file.SourceChapterId is null;
        var action = plan.Resolve(PageConversionPlanService.Candidate(file,
            await PageConversionPlanService.SeriesFactsAsync(db, file.SeriesId, ct)));
        if (action is not PlanAction.Convert { Settings: var settings })
        {
            await MarkCheckedAsync(file, action.Name, ct);
            return new FileConversion(FileConversionStatus.Unchanged);
        }

        // Left unchecked: whether it is still due when the next pass comes round can change.
        if (await CleanupDueAsync(file, cache, ct))
        {
            return new FileConversion(FileConversionStatus.Unchanged);
        }

        // A pass reads each file end to end once and never again soon, so whatever it leaves as it was goes
        // out of the page cache. A queued file is a fresh download, likely to be read next.
        void LeaveCold()
        {
            if (!queued)
            {
                DropCache(path);
            }
        }

        if (!await Probe(path, settings, ct))
        {
            await MarkCheckedAsync(file, action.Name, ct);
            LeaveCold();
            return new FileConversion(FileConversionStatus.Unchanged, Outcome: ArchiveConversionOutcome.NotSmaller);
        }

        var before = new FileInfo(path);
        var (length, written) = (before.Length, before.LastWriteTimeUtc);
        var tmp = Path.Combine(row.Root, ".maki", "tmp", $"{TrashPrefix}{fileId.ToString(CultureInfo.InvariantCulture)}.cbz");
        var hardlinks = true;
        if (keepOriginal && !cache.Hardlinks.TryGetValue(row.Root, out hardlinks))
        {
            hardlinks = HardlinksHoldUp(row.Root);
            cache.Hardlinks[row.Root] = hardlinks;
        }

        if (!HasRoomToSwap(row.Root, file.RelativePath, length, hardlinks))
        {
            return new FileConversion(FileConversionStatus.Failed);
        }

        var source = CopiesSource ? Path.ChangeExtension(tmp, ".source.cbz") : path;
        ArchiveConversionResult result;
        try
        {
            if (source != path)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(source)!);
                File.Copy(path, source, overwrite: true);
                // The copy keeps the library file's write time, which the housekeeping tmp sweep would
                // read as a day-old leftover and delete mid-conversion.
                File.SetLastWriteTimeUtc(source, DateTime.UtcNow);
            }

            result = await Converter(source, tmp, settings, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not convert {Path}", path);
            LeaveCold();
            return new FileConversion(FileConversionStatus.Failed);
        }
        finally
        {
            if (source != path)
            {
                TryDelete(source);
            }
        }

        if (result.Outcome != ArchiveConversionOutcome.Converted)
        {
            await MarkCheckedAsync(file, action.Name, ct);
            LeaveCold();
            return new FileConversion(FileConversionStatus.Unchanged, Outcome: result.Outcome);
        }

        var swapped = false;

        try
        {
            using var seriesLock = await SeriesLocks.SeriesAsync(file.SeriesId, ct);
            var current = await db.ChapterFiles.IgnoreQueryFilters().FirstOrDefaultAsync(f => f.Id == fileId, ct);
            var onDisk = new FileInfo(path);
            if (current is null || current.RelativePath != file.RelativePath || current.ConversionCheckedAtUtc is not null ||
                !onDisk.Exists || onDisk.Length != length || onDisk.LastWriteTimeUtc != written ||
                await BusyAsync(current, queued, ct) || (queued && archives.ReadWithin(path, ReadingQuiet)))
            {
                LeaveCold();
                return new FileConversion(FileConversionStatus.Busy);
            }

            var chapterId = await db.Chapters.IgnoreQueryFilters()
                .Where(c => c.ChapterFileId == current.Id)
                .OrderBy(c => c.Id)
                .Select(c => (int?)c.Id)
                .FirstOrDefaultAsync(ct);
            var trashRelative = keepOriginal ? Swap(row.Root, current, path, tmp, hardlinks) : null;
            if (keepOriginal ? trashRelative is null : !Replace(path, tmp))
            {
                LeaveCold();
                return new FileConversion(FileConversionStatus.Failed);
            }

            swapped = true;

            // The bytes have changed: from here the row has to be saved whatever happens.
            archives.Invalidate(current.Id);
            var acquired = current.MeasuredAtUtc is null ? null : current.ImageFormat;
            var now = DateTime.UtcNow;
            PageConversionOptions.Apply(current, result, acquired, action.Name, now);
            current.Size = new FileInfo(path).Length;
            current.ConversionQueuedAtUtc = null;
            if (trashRelative is not null)
            {
                db.PageConversionHistory.Add(new PageConversionHistory
                {
                    ChapterFileId = current.Id,
                    SeriesId = current.SeriesId,
                    ChapterId = chapterId,
                    PassId = cache.PassId,
                    TrashPath = trashRelative,
                    TrashBytes = length,
                    Action = action.Name,
                    BytesBefore = length,
                    BytesAfter = current.Size,
                    SourceFormat = result.SourceFormat,
                    ResultFormat = result.ResultFormat,
                    CreatedAtUtc = now
                });
            }

            await db.SaveChangesAsync(CancellationToken.None);
        }
        finally
        {
            TryDelete(tmp);
            // The converted copy was flushed before it was renamed in, so its pages are clean and can go.
            if (swapped)
            {
                LeaveCold();
            }
        }

        var topFolder = file.RelativePath.Split('/', '\\')[0];
        kavitaScans.QueueScan(Path.Combine(row.Root, topFolder), file.SeriesId);
        logger.LogInformation("Converted the pages of {Path}: {Before} to {After} bytes",
            file.RelativePath, result.SourceBytes, result.ResultBytes);
        return new FileConversion(FileConversionStatus.Converted, result.SavedBytes, result.Outcome);
    }

    /// <summary>
    /// Puts the original into the trash and the converted copy at its path: a hardlink (or, where the
    /// probe found none that hold up, a copy) into the trash first, then a rename over the original, so the
    /// library path holds a whole archive at every step. Moving the original out instead would leave
    /// the path empty whenever moving a file back in failed too. Returns the trash copy's root-relative
    /// path, or null when the file was left as it was.
    /// </summary>
    private string? Swap(string root, ChapterFile file, string path, string converted, bool hardlinks)
    {
        UpgradeTrash.EnsureFolder(root, file.SeriesId);
        var trashRelative = UpgradeTrash.NewRelativePath(root, file.SeriesId,
            $"{TrashPrefix}{file.Id.ToString(CultureInfo.InvariantCulture)}", Path.GetFileName(path));
        var trash = LibraryPaths.Resolve(root, trashRelative)!;
        try
        {
            if (!hardlinks || !Hardlink(path, trash))
            {
                File.Copy(path, trash);
            }

            try
            {
                MoveOver(converted, path);
            }
            // A share can report a failure for a rename that happened (an NFS retransmit).
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && SameVolumeMove.Landed(converted, path))
            {
                logger.LogDebug(ex, "Replacing {Path} reported a failure but the converted copy is in place", path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(trash);
            logger.LogWarning(ex, "Could not replace {Path} with its converted copy; left as it was", path);
            return null;
        }

        try
        {
            File.SetLastWriteTimeUtc(trash, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not stamp {Path} with its trash time", trash);
        }

        // The original, the same inode when hardlinked, is only read again by an undo.
        DropCache(trash);
        return trashRelative;
    }

    /// <summary>Renames the converted copy over the original, which is not kept. False when the file was left as it was.</summary>
    private bool Replace(string path, string converted)
    {
        try
        {
            MoveOver(converted, path);
            return true;
        }
        catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && SameVolumeMove.Landed(converted, path))
        {
            logger.LogDebug(ex, "Replacing {Path} reported a failure but the converted copy is in place", path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not replace {Path} with its converted copy; left as it was", path);
            return false;
        }
    }

    /// <summary>
    /// Without hardlinks the swap copies the original into the trash after the encode, and where the
    /// converter reads a copy of the source that copy sits beside the converted one during the encode.
    /// Either way the volume needs room for a second whole copy next to the converted one. Checked
    /// before anything is copied or encoded, so a full volume skips the file instead of filling up and
    /// failing on every pass.
    /// </summary>
    private bool HasRoomToSwap(string root, string relativePath, long length, bool hardlinks)
    {
        if (!hardlinks)
        {
            return HasRoomFor(root, relativePath, length,
                "this volume has no working hardlinks, so the original is copied into the trash");
        }

        return !CopiesSource || HasRoomFor(root, relativePath, length,
            "the source is copied into .maki/tmp to be read");
    }

    /// <summary>
    /// Hardlinks a scratch file in <c>.maki/tmp</c>, deletes the link and reads the scratch file back.
    /// On a Docker Desktop for Windows bind mount the link and the delete both succeed and then the
    /// first name is gone for the running container, though still on disk, so a link that was made
    /// proves nothing. A library file is never used for this: the probe itself can break it. The
    /// name is fixed because on that mount the scratch file outlives its delete.
    /// </summary>
    internal bool HardlinksHoldUp(string root)
    {
        var tmp = Path.Combine(root, ".maki", "tmp");
        var scratch = Path.Combine(tmp, $"{TrashPrefix}hardlink.probe");
        var link = scratch + ".link";
        var bytes = "maki hardlink probe"u8.ToArray();
        try
        {
            Directory.CreateDirectory(tmp);
            File.WriteAllBytes(scratch, bytes);
            if (!Hardlink(scratch, link))
            {
                return false;
            }

            File.Delete(link);
            return File.ReadAllBytes(scratch).AsSpan().SequenceEqual(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "The hardlink probe in {Folder} failed, so originals are copied into the trash", tmp);
            return false;
        }
        finally
        {
            TryDelete(link);
            TryDelete(scratch);
        }
    }

    private bool HasRoomFor(string root, string relativePath, long length, string why)
    {
        var needed = 2 * length + PageConversionWorker.DiskReserveBytes;
        if (FreeSpace(root) is not { } free || free >= needed)
        {
            return true;
        }

        logger.LogWarning("Skipped converting {Path}: {Why}, which needs {Needed} bytes free and {Free} are",
            relativePath, why, needed, free);
        return false;
    }

    /// <summary>
    /// A download in flight for the series, or with <paramref name="queued"/> only one that can touch this
    /// file: a chapter on it, or a row with no chapter (a torrent), which may place anything in the series.
    /// </summary>
    private Task<bool> BusyAsync(ChapterFile file, bool queued, CancellationToken ct)
    {
        var inFlight = SeriesLocks.InFlight(db.DownloadQueue.IgnoreQueryFilters()).Where(q => q.SeriesId == file.SeriesId);
        return queued
            ? inFlight.AnyAsync(q => q.ChapterId == null ||
                                     db.Chapters.IgnoreQueryFilters().Any(c => c.Id == q.ChapterId && c.ChapterFileId == file.Id), ct)
            : inFlight.AnyAsync(ct);
    }

    /// <summary>
    /// Whether read-file cleanup will delete the file within <see cref="CleanupHorizonDays"/>, by its own
    /// schedule, worked out once per series per pass.
    /// </summary>
    private async Task<bool> CleanupDueAsync(ChapterFile file, PageConversionPassCache cache, CancellationToken ct)
    {
        if (!cache.CleanupDue.TryGetValue(file.SeriesId, out var due))
        {
            due = await CleanupDueFilesAsync(db, appSettings, file.SeriesId, ct);
            cache.CleanupDue[file.SeriesId] = due;
        }

        return due.Contains(file.Id);
    }

    /// <summary>The files of a series read-file cleanup deletes within <see cref="CleanupHorizonDays"/>.</summary>
    public static async Task<HashSet<int>> CleanupDueFilesAsync(MakiDbContext db, IAppSettings appSettings, int seriesId,
        CancellationToken ct)
    {
        var horizon = DateTime.UtcNow.AddDays(CleanupHorizonDays);
        var chapters = (await ReadFileCleanupService.PendingAsync(db, appSettings, seriesId, ct))
            .Where(c => c.Value <= horizon)
            .Select(c => c.Key)
            .ToList();
        return chapters.Count == 0
            ? []
            : (await db.Chapters.IgnoreQueryFilters()
                .Where(c => chapters.Contains(c.Id) && c.ChapterFileId != null)
                .Select(c => c.ChapterFileId!.Value)
                .ToListAsync(ct)).ToHashSet();
    }

    /// <summary>Matches nothing when a download or relink changed the row since it was read.</summary>
    private Task MarkCheckedAsync(ChapterFile file, string checkedWith, CancellationToken ct) =>
        db.ChapterFiles.IgnoreQueryFilters()
            .Where(f => f.Id == file.Id && f.RelativePath == file.RelativePath && f.Size == file.Size &&
                        f.ConversionCheckedAtUtc == null)
            .ExecuteUpdateAsync(set => set
                .SetProperty(f => f.ConversionCheckedAtUtc, DateTime.UtcNow)
                .SetProperty(f => f.CheckedWith, checkedWith), ct);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
