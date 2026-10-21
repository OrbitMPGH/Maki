using System.Globalization;
using Maki.Core.Entities;
using Maki.Core.Images;
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

/// <summary>
/// Converts the pages of one library file in place. The original goes to the upgrade trash
/// (<c>.maki-trash/&lt;SeriesId&gt;/conv-&lt;FileId&gt;-&lt;name&gt;</c>) with no history row, so the
/// trash purge removes it after <c>upgrades.trashRetentionDays</c>. The converted copy is built
/// under <c>&lt;root&gt;/.maki/tmp</c>, outside the series lock, and swapped in under it with a rename,
/// so a crash leaves the library path holding one complete archive or the other.
/// </summary>
public class PageConversionService(
    MakiDbContext db, ReaderArchiveCache archives, KavitaScanService kavitaScans,
    ILogger<PageConversionService> logger)
{
    public const string TrashPrefix = "conv-";

    internal Func<string, string, bool> Hardlink { get; init; } = FileLinker.TryHardlink;

    internal Action<string, string> MoveOver { get; init; } = (from, to) => File.Move(from, to, overwrite: true);

    internal Func<string, long?> FreeSpace { get; init; } = DiskSpace.AvailableFor;

    internal Func<string, string, PageConversionSettings, CancellationToken, Task<ArchiveConversionResult>> Converter { get; init; } =
        ArchivePageConverter.ConvertAsync;

    public async Task<FileConversion> ConvertAsync(int fileId, PageConversionSettings settings, CancellationToken ct)
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
            await MarkCheckedAsync(file, ct);
            return new FileConversion(FileConversionStatus.Unchanged);
        }

        if (!File.Exists(path))
        {
            return new FileConversion(FileConversionStatus.Failed);
        }

        if (HardLinks.IsShared(path))
        {
            await MarkCheckedAsync(file, ct);
            return new FileConversion(FileConversionStatus.Hardlinked);
        }

        if (await BusyAsync(file.SeriesId, ct))
        {
            return new FileConversion(FileConversionStatus.Busy);
        }

        var before = new FileInfo(path);
        var (length, written) = (before.Length, before.LastWriteTimeUtc);
        var tmp = Path.Combine(row.Root, ".maki", "tmp", $"{TrashPrefix}{fileId.ToString(CultureInfo.InvariantCulture)}.cbz");
        if (!HasRoomToSwap(row.Root, file.RelativePath, path, tmp, length))
        {
            return new FileConversion(FileConversionStatus.Failed);
        }

        // Windows refuses to rename an open file or move the folder it is in, and a conversion can take
        // minutes, so there it reads a copy rather than holding the library file.
        var source = OperatingSystem.IsWindows() ? Path.ChangeExtension(tmp, ".source.cbz") : path;
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
            await MarkCheckedAsync(file, ct);
            return new FileConversion(FileConversionStatus.Unchanged, Outcome: result.Outcome);
        }

        try
        {
            using var seriesLock = await SeriesLocks.SeriesAsync(file.SeriesId, ct);
            var current = await db.ChapterFiles.IgnoreQueryFilters().FirstOrDefaultAsync(f => f.Id == fileId, ct);
            var now = new FileInfo(path);
            if (current is null || current.RelativePath != file.RelativePath || current.ConversionCheckedAtUtc is not null ||
                !now.Exists || now.Length != length || now.LastWriteTimeUtc != written || await BusyAsync(file.SeriesId, ct))
            {
                return new FileConversion(FileConversionStatus.Busy);
            }

            if (!Swap(row.Root, current, path, tmp))
            {
                return new FileConversion(FileConversionStatus.Failed);
            }

            // The bytes have changed: from here the row has to be saved whatever happens.
            archives.Invalidate(current.Id);
            var acquired = current.MeasuredAtUtc is null ? null : current.ImageFormat;
            PageConversionOptions.Apply(current, result, acquired, DateTime.UtcNow);
            current.Size = new FileInfo(path).Length;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        finally
        {
            TryDelete(tmp);
        }

        var topFolder = file.RelativePath.Split('/', '\\')[0];
        kavitaScans.QueueScan(Path.Combine(row.Root, topFolder), file.SeriesId);
        logger.LogInformation("Converted the pages of {Path}: {Before} to {After} bytes",
            file.RelativePath, result.SourceBytes, result.ResultBytes);
        return new FileConversion(FileConversionStatus.Converted, result.SavedBytes, result.Outcome);
    }

    /// <summary>
    /// Puts the original into the trash and the converted copy at its path: a hardlink (or, where the
    /// filesystem has none, a copy) into the trash first, then a rename over the original, so the
    /// library path holds a whole archive at every step. Moving the original out instead would leave
    /// the path empty whenever moving a file back in failed too.
    /// </summary>
    private bool Swap(string root, ChapterFile file, string path, string converted)
    {
        UpgradeTrash.EnsureFolder(root, file.SeriesId);
        var trashRelative = UpgradeTrash.NewRelativePath(root, file.SeriesId,
            $"{TrashPrefix}{file.Id.ToString(CultureInfo.InvariantCulture)}", Path.GetFileName(path));
        var trash = LibraryPaths.Resolve(root, trashRelative)!;
        try
        {
            if (!Hardlink(path, trash))
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
            return false;
        }

        try
        {
            File.SetLastWriteTimeUtc(trash, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not stamp {Path} with its trash time", trash);
        }

        return true;
    }

    /// <summary>
    /// Without hardlinks the swap copies the original into the trash after the encode, so the volume
    /// needs room for that copy beside the converted one. Checked before the encode, so a full share
    /// skips the file instead of encoding it on every pass only to fail at the copy.
    /// </summary>
    private bool HasRoomToSwap(string root, string relativePath, string path, string tmp, long length)
    {
        var probe = Path.ChangeExtension(tmp, ".probe");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(probe)!);
            TryDelete(probe);
            if (Hardlink(path, probe))
            {
                TryDelete(probe);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The encode and the swap report their own failure.
            return true;
        }

        var needed = 2 * length + PageConversionWorker.DiskReserveBytes;
        if (FreeSpace(root) is not { } free || free >= needed)
        {
            return true;
        }

        logger.LogWarning(
            "Skipped converting {Path}: this volume has no hardlinks, so the original is copied into the trash, which needs {Needed} bytes free and {Free} are",
            relativePath, needed, free);
        return false;
    }

    private Task<bool> BusyAsync(int seriesId, CancellationToken ct) =>
        SeriesLocks.InFlight(db.DownloadQueue.IgnoreQueryFilters()).AnyAsync(q => q.SeriesId == seriesId, ct);

    /// <summary>Matches nothing when a download or relink changed the row since it was read.</summary>
    private Task MarkCheckedAsync(ChapterFile file, CancellationToken ct) =>
        db.ChapterFiles.IgnoreQueryFilters()
            .Where(f => f.Id == file.Id && f.RelativePath == file.RelativePath && f.Size == file.Size &&
                        f.ConversionCheckedAtUtc == null)
            .ExecuteUpdateAsync(set => set.SetProperty(f => f.ConversionCheckedAtUtc, DateTime.UtcNow), ct);

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
