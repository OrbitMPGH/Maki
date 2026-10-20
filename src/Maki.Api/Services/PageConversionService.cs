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
        ArchiveConversionResult result;
        try
        {
            result = await ArchivePageConverter.ConvertAsync(path, tmp, settings, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not convert {Path}", path);
            return new FileConversion(FileConversionStatus.Failed);
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
    /// Puts the original into the trash and the converted copy at its path. A hardlink into the trash
    /// first, then a rename over the original, keeps the library path filled at every step; where the
    /// filesystem has no hardlinks the original is moved out and the copy moved in, and moved back on a
    /// failure.
    /// </summary>
    private bool Swap(string root, ChapterFile file, string path, string converted)
    {
        UpgradeTrash.EnsureFolder(root, file.SeriesId);
        var trashRelative = UpgradeTrash.NewRelativePath(root, file.SeriesId,
            $"{TrashPrefix}{file.Id.ToString(CultureInfo.InvariantCulture)}", Path.GetFileName(path));
        var trash = LibraryPaths.Resolve(root, trashRelative)!;
        try
        {
            if (FileLinker.TryHardlink(path, trash))
            {
                try
                {
                    File.Move(converted, path, overwrite: true);
                }
                catch
                {
                    TryDelete(trash);
                    throw;
                }
            }
            else
            {
                File.Move(path, trash);
                try
                {
                    File.Move(converted, path);
                }
                catch
                {
                    File.Move(trash, path);
                    throw;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
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
