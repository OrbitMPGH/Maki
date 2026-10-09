using System.Globalization;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Deletes the files of chapters everyone has finished, a set number of days after the last of them
/// did. Opt-in twice over: the instance switch is off by default and a series can turn it on or off
/// for itself (<see cref="Series.ReadFileCleanup"/>). Deletion goes through
/// <see cref="ChapterFileDeletion"/>, so reads stay and nothing downloads the chapters again.
/// <para>
/// A chapter qualifies only when every user with progress in the series has completed it. A user
/// with no progress there at all doesn't hold anything back, and a series nobody has read has
/// nothing to clean. A volume file goes only when every chapter on it qualifies.
/// </para>
/// </summary>
public class ReadFileCleanupService(
    MakiDbContext db,
    IAppSettings settings,
    ChapterFileDeletion deletion,
    TimeProvider time,
    ILogger<ReadFileCleanupService> logger)
{
    public const int DefaultDays = 7;
    public const int MaxDays = 365;

    public sealed record Options(bool Enabled, int Days, bool KeepLast);

    public async Task<Options> OptionsAsync(CancellationToken ct) => new(
        await settings.GetAsync(SettingKeys.ReadFileCleanupEnabled, ct) == "true",
        int.TryParse(await settings.GetAsync(SettingKeys.ReadFileCleanupDays, ct), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var days) && days is >= 1 and <= MaxDays
            ? days
            : DefaultDays,
        await settings.GetAsync(SettingKeys.ReadFileCleanupKeepLast, ct) != "false");

    public static bool AppliesTo(ReadFileCleanup mode, Options options) =>
        mode == ReadFileCleanup.On || (mode == ReadFileCleanup.Default && options.Enabled);

    /// <summary>
    /// Every chapter of <paramref name="seriesId"/> whose file is on course to be deleted, with when.
    /// Query filters are off: the plan has to see every reader's progress, not the caller's.
    /// </summary>
    public async Task<Dictionary<int, DateTime>> ScheduleAsync(int seriesId, Options options, CancellationToken ct)
    {
        var chapters = await db.Chapters.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.SeriesId == seriesId && c.ChapterFileId != null)
            .Select(c => new { c.Id, FileId = c.ChapterFileId!.Value })
            .ToListAsync(ct);
        if (chapters.Count == 0)
        {
            return [];
        }

        var progress = await db.ChapterProgress.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.SeriesId == seriesId)
            .Select(p => new { p.UserId, p.ChapterId, p.Completed, p.Watched, p.CompletedAt, p.UpdatedAt, p.PageIndex, p.PageCount })
            .ToListAsync(ct);
        var readers = progress.Select(p => p.UserId).Distinct().ToList();
        if (readers.Count == 0)
        {
            return [];
        }

        // A watched tick is "seen elsewhere", not read here, so it never makes a file due.
        // A re-read to the end keeps the first completion stamp but moves UpdatedAt, so for a row
        // sitting at its end the later of the two is when the chapter was last read. Any other save
        // on a completed row (a chapter merely opened, an OPDS prefetch) says nothing about that.
        var read = progress
            .Where(p => p.Completed && !p.Watched && p.CompletedAt != null)
            .Select(p => new
            {
                p.UserId,
                p.ChapterId,
                LastRead = p.PageCount > 0 && p.PageIndex >= p.PageCount - 1 && p.UpdatedAt > p.CompletedAt!.Value
                    ? p.UpdatedAt
                    : p.CompletedAt!.Value,
            })
            .ToList();
        var finished = read.ToLookup(p => p.ChapterId);

        // Each reader's most recent finish, so they can look back at where they stopped. A bulk write
        // stamps its rows a few ticks apart in no useful order, so finishes inside the same second
        // are told apart by chapter number.
        HashSet<int> kept = [];
        if (options.KeepLast)
        {
            var numbers = await db.Chapters.IgnoreQueryFilters().AsNoTracking()
                .Where(c => c.SeriesId == seriesId)
                .Select(c => new { c.Id, c.Number })
                .ToDictionaryAsync(c => c.Id, c => c.Number ?? decimal.MinValue, ct);
            kept = read
                .GroupBy(p => p.UserId)
                .Select(g => g.MaxBy(p => (p.LastRead.Ticks / TimeSpan.TicksPerSecond,
                    numbers.GetValueOrDefault(p.ChapterId, decimal.MinValue)))!.ChapterId)
                .ToHashSet();
        }

        var due = new Dictionary<int, DateTime>();
        foreach (var file in chapters.GroupBy(c => c.FileId))
        {
            DateTime? fileDue = null;
            foreach (var chapter in file)
            {
                var finishes = finished[chapter.Id].ToList();
                if (kept.Contains(chapter.Id) || readers.Any(u => finishes.All(p => p.UserId != u)))
                {
                    fileDue = null;
                    break;
                }

                var at = finishes.Max(p => p.LastRead).AddDays(options.Days);
                fileDue = fileDue is { } sofar && sofar > at ? sofar : at;
            }

            if (fileDue is { } when)
            {
                foreach (var chapter in file)
                {
                    due[chapter.Id] = when;
                }
            }
        }

        return due;
    }

    /// <summary>Deletes every file that is due, series by series. Returns how many files were deleted from disk.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var options = await OptionsAsync(ct);
        var seriesIds = await db.Series.IgnoreQueryFilters()
            .Where(s => s.ReadFileCleanup == ReadFileCleanup.On ||
                        (options.Enabled && s.ReadFileCleanup == ReadFileCleanup.Default))
            .Select(s => s.Id)
            .ToListAsync(ct);

        var total = 0;
        foreach (var seriesId in seriesIds)
        {
            var now = time.GetUtcNow().UtcDateTime;
            if (!(await ScheduleAsync(seriesId, options, ct)).Values.Any(at => at <= now))
            {
                continue;
            }

            try
            {
                total += await CleanSeriesAsync(seriesId, options, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Read file cleanup failed for series {SeriesId}", seriesId);
            }
        }

        return total;
    }

    private async Task<int> CleanSeriesAsync(int seriesId, Options options, CancellationToken ct)
    {
        using var seriesLock = await SeriesLocks.SeriesAsync(seriesId, ct);

        // Planned again under the lock: a rescan or download may have moved files since.
        var now = time.GetUtcNow().UtcDateTime;
        var dueChapters = (await ScheduleAsync(seriesId, options, ct))
            .Where(d => d.Value <= now)
            .Select(d => d.Key)
            .ToList();
        if (dueChapters.Count == 0 ||
            await SeriesLocks.InFlight(db.DownloadQueue)
                .AnyAsync(q => q.SeriesId == seriesId, ct))
        {
            return 0;
        }

        var series = await db.Series.IgnoreQueryFilters().Include(s => s.RootFolder)
            .FirstAsync(s => s.Id == seriesId, ct);
        var files = await db.ChapterFiles.IgnoreQueryFilters()
            .Where(f => db.Chapters.Any(c => c.ChapterFileId == f.Id && dueChapters.Contains(c.Id)))
            .ToListAsync(ct);

        var result = await deletion.DeleteAsync(series, files, ct);
        logger.LogInformation(
            "Read file cleanup for {Title}: deleted {Deleted} files, kept {Kept} another series still uses, {Failed} failed, {Chapters} chapters marked removed",
            series.Title, result.Deleted, result.Kept, result.Failed, result.ChaptersRemoved);
        return result.Deleted;
    }
}
