using System.Globalization;
using Maki.Api.Localization;
using Maki.Core.Entities;
using Maki.Core.Images;
using Maki.Core.Paths;
using Maki.Core.Storage;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <param name="ConvertedFiles">Files of the series the pass or a download converted.</param>
/// <param name="RestorableFiles">Converted files whose original still waits in the trash.</param>
/// <param name="CheckedWith">The settings those restorable files were converted at.</param>
public sealed record SeriesConversionDto(
    int ConvertedFiles, long SavedBytes, int RestorableFiles, long RestorableBytes, IReadOnlyList<string> CheckedWith);

/// <param name="Reasons">
/// One line per file passed over, one for the older conversions that no longer apply, and one when
/// files were queued to convert again.
/// </param>
public sealed record UndoResultDto(int Restored, int Skipped, IReadOnlyList<string> Reasons);

/// <summary>
/// Puts a series' converted files back to the originals the library pass left in the trash. Each
/// file is swapped under the series lock: the converted copy goes aside into <c>.maki/tmp</c>, the
/// original comes back to its path and only then is the aside copy deleted, so the library path is
/// empty only between two renames on the same volume.
/// </summary>
public class PageConversionUndoService(
    MakiDbContext db, ReaderArchiveCache archives, KavitaScanService kavitaScans, PageConversionWorker worker,
    PageConversionPlanService plans, ILocalizer localizer, ILogger<PageConversionUndoService> logger)
{
    public const string AsidePrefix = "conv-undo-";

    /// <summary>Not a <c>.cbz</c>, so the day-old tmp sweep leaves a converted copy a failed rollback stranded.</summary>
    public const string AsideExtension = ".cbz.aside";

    internal Func<string, string, bool>? MoveFile { get; init; }

    /// <summary>Null when the series does not exist for the caller.</summary>
    public async Task<SeriesConversionDto?> HistoryAsync(int seriesId, CancellationToken ct)
    {
        if (await RootAsync(seriesId, ct) is null)
        {
            return null;
        }

        var converted = db.ChapterFiles.IgnoreQueryFilters()
            .Where(f => f.SeriesId == seriesId && f.OriginalImageFormat != null);
        var restorable = await Restorable(db, seriesId)
            .Select(h => new { h.TrashBytes, h.Action })
            .ToListAsync(ct);
        return new SeriesConversionDto(
            await converted.CountAsync(ct),
            await converted.SumAsync(f => f.ConversionSavedBytes, ct),
            restorable.Count,
            restorable.Sum(r => r.TrashBytes ?? 0),
            [.. restorable.Select(r => r.Action).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Restores every converted file of the series whose original is still in the trash. A restored
    /// file is stamped <see cref="PlanAction.UndoneName"/>, so the pass leaves it alone, unless
    /// <paramref name="convertAgain"/> puts it back in line for the next pass and starts one.
    /// Null when the series does not exist for the caller.
    /// </summary>
    public async Task<UndoResultDto?> UndoAsync(int seriesId, bool setNever, bool convertAgain, CancellationToken ct)
    {
        if (await RootAsync(seriesId, ct) is not { } root)
        {
            return null;
        }

        var reasons = new List<string>();
        var restored = new List<ChapterFile>();
        var skipped = 0;
        using (await SeriesLocks.SeriesAsync(seriesId, ct))
        {
            var rows = await Restorable(db, seriesId).AsNoTracking().OrderBy(h => h.Id).ToListAsync(ct);
            var stale = (await db.PageConversionHistory.IgnoreQueryFilters()
                    .Where(h => h.SeriesId == seriesId && h.TrashPath != null && h.UndoneAtUtc == null)
                    .Select(h => h.ChapterFileId)
                    .Distinct()
                    .ToListAsync(ct))
                .Except(rows.Select(r => r.ChapterFileId))
                .Count();
            if (rows.Count > 0 &&
                await SeriesLocks.InFlight(db.DownloadQueue.IgnoreQueryFilters()).AnyAsync(q => q.SeriesId == seriesId, ct))
            {
                skipped = rows.Count;
                reasons.Add(localizer.Get("pageConversion.undo.downloadInFlight"));
                rows = [];
            }

            foreach (var row in rows)
            {
                // Once a file has moved, the rest are done too rather than cancelled halfway.
                if (await RestoreAsync(root, seriesId, row, convertAgain, restored.Count == 0 ? ct : CancellationToken.None)
                    is { } reason)
                {
                    skipped++;
                    reasons.Add(localizer.Get(reason, new { name = DisplayName(row) }));
                }
                else
                {
                    restored.Add(await db.ChapterFiles.IgnoreQueryFilters().SingleAsync(f => f.Id == row.ChapterFileId,
                        CancellationToken.None));
                }
            }

            if (stale > 0)
            {
                reasons.Add(localizer.Get("pageConversion.undo.stale", new { count = stale }));
            }

            if (setNever)
            {
                var series = await db.Series.IgnoreQueryFilters().SingleAsync(s => s.Id == seriesId, CancellationToken.None);
                if (series.PageConversion != PageConversionOverride.Never)
                {
                    series.PageConversion = PageConversionOverride.Never;
                    await db.SaveChangesAsync(CancellationToken.None);
                    await plans.RecheckAsync(await PageConversionOptions.LoadPlanAsync(db, CancellationToken.None), seriesId,
                        CancellationToken.None);
                }
            }
        }

        foreach (var folder in restored.Select(f => f.RelativePath.Split('/', '\\')[0]).Distinct(StringComparer.Ordinal))
        {
            kavitaScans.QueueScan(Path.Combine(root, folder), seriesId);
        }

        if (convertAgain && restored.Count > 0)
        {
            reasons.Add(localizer.Get("pageConversion.undo.queued", new { count = restored.Count }));
            if (await PageConversionOptions.LoadPlanAsync(db, CancellationToken.None) is not null &&
                await worker.StateAsync(CancellationToken.None) == PageConversionPassState.Idle)
            {
                await worker.StartPassAsync(CancellationToken.None);
            }
        }

        logger.LogInformation("Undid page conversion on series {Series}: {Restored} restored, {Skipped} passed over",
            seriesId, restored.Count, skipped);
        return new UndoResultDto(restored.Count, skipped, reasons);
    }

    /// <summary>Null when the file was restored, else the reason key it was passed over for.</summary>
    private async Task<string?> RestoreAsync(string root, int seriesId, PageConversionHistory row, bool convertAgain,
        CancellationToken ct)
    {
        var file = await db.ChapterFiles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(f => f.Id == row.ChapterFileId && f.SeriesId == seriesId, ct);
        if (file is null)
        {
            return "pageConversion.undo.fileGone";
        }

        var library = LibraryPaths.Resolve(root, file.RelativePath);
        if (library is null || file.OriginalImageFormat is null || file.Size != row.BytesAfter)
        {
            return "pageConversion.undo.fileChanged";
        }

        // An earlier undo whose rollback failed left the only converted copy aside.
        var aside = AsidePath(root, file.Id);
        if (!File.Exists(library) && File.Exists(aside) && !(MoveFile ?? Move)(aside, library))
        {
            logger.LogWarning("Could not put {Aside} back at {Path}", aside, library);
            return "pageConversion.undo.moveFailed";
        }

        if (TrashFile(root, row) is not { } trash)
        {
            return "pageConversion.undo.trashGone";
        }

        if (!File.Exists(library) || new FileInfo(library).Length != row.BytesAfter)
        {
            return "pageConversion.undo.fileChanged";
        }

        if (!Swap(root, file.Id, library, trash))
        {
            return "pageConversion.undo.moveFailed";
        }

        // The bytes have changed: from here the rows have to be saved whatever happens.
        archives.Invalidate(file.Id);
        var now = DateTime.UtcNow;
        file.ImageFormat = file.OriginalImageFormat ?? file.ImageFormat;
        file.ResetConversion();
        file.Size = new FileInfo(library).Length;
        if (!convertAgain)
        {
            file.ConversionCheckedAtUtc = now;
            file.CheckedWith = PlanAction.UndoneName;
        }

        db.PageConversionHistory.Attach(row);
        row.UndoneAtUtc = now;
        row.TrashPath = null;
        row.TrashBytes = null;
        await db.SaveChangesAsync(CancellationToken.None);
        return null;
    }

    /// <summary>
    /// The converted copy aside, the original back over its path, then the aside copy deleted. When
    /// the original cannot come back the converted copy returns to its path.
    /// </summary>
    private bool Swap(string root, int fileId, string library, string trash)
    {
        var aside = AsidePath(root, fileId);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(aside)!);
            // A leftover from an undo that died between its renames: the library path holds the file the
            // checks just measured, so this copy is stale.
            File.Delete(aside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not prepare {Aside} to undo the conversion of {Path}", aside, library);
            return false;
        }

        var move = MoveFile ?? Move;
        if (!move(library, aside))
        {
            return false;
        }

        try
        {
            // A rename keeps the old write time, and the copy may have to wait here for the next undo.
            File.SetLastWriteTimeUtc(aside, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not stamp {Aside}", aside);
        }

        if (!move(trash, library))
        {
            if (!move(aside, library))
            {
                logger.LogWarning("Could not put the converted copy of {Path} back; it is at {Aside} until the next undo",
                    library, aside);
            }

            return false;
        }

        try
        {
            File.Delete(aside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not delete {Aside}; the tmp sweep takes it", aside);
        }

        return true;
    }

    private bool Move(string from, string to)
    {
        var outcome = SameVolumeMove.Move(from, to);
        if (outcome.Result == MoveResult.Moved || SameVolumeMove.Landed(from, to))
        {
            return true;
        }

        logger.LogWarning(outcome.Error, "Could not move {From} to {To}: {Result}", from, to, outcome.Result);
        return false;
    }

    private Task<string?> RootAsync(int seriesId, CancellationToken ct) =>
        db.Series.Where(s => s.Id == seriesId).Select(s => s.RootFolder!.Path).FirstOrDefaultAsync(ct);

    /// <summary>
    /// The rows an undo can act on: the newest row of its file, still naming a trash copy and not undone,
    /// whose file is in the series and still the converted copy the row made. Read from the database only;
    /// the trash copy itself is checked when the undo reaches it.
    /// </summary>
    public static IQueryable<PageConversionHistory> Restorable(MakiDbContext db, int? seriesId)
    {
        var rows = db.PageConversionHistory.IgnoreQueryFilters();
        if (seriesId is { } only)
        {
            rows = rows.Where(h => h.SeriesId == only);
        }

        return rows.Where(h => h.TrashPath != null && h.UndoneAtUtc == null &&
                               !db.PageConversionHistory.Any(n => n.ChapterFileId == h.ChapterFileId && n.Id > h.Id) &&
                               db.ChapterFiles.Any(f => f.Id == h.ChapterFileId && f.SeriesId == h.SeriesId &&
                                                        f.OriginalImageFormat != null && f.Size == h.BytesAfter));
    }

    public static string AsidePath(string root, int fileId) =>
        Path.Combine(root, ".maki", "tmp", $"{AsidePrefix}{fileId.ToString(CultureInfo.InvariantCulture)}{AsideExtension}");

    private static string? TrashFile(string root, PageConversionHistory row) =>
        row.TrashPath is { } relative && LibraryPaths.Resolve(root, relative) is { } path && File.Exists(path) ? path : null;

    /// <summary>The library file's name, read off the trash copy's <c>conv-&lt;id&gt;-&lt;name&gt;</c>.</summary>
    private static string DisplayName(PageConversionHistory row)
    {
        var name = Path.GetFileName(row.TrashPath ?? "");
        var prefix = $"{PageConversionService.TrashPrefix}{row.ChapterFileId.ToString(CultureInfo.InvariantCulture)}-";
        return name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name;
    }
}
