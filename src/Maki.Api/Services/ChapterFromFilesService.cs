using System.Globalization;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Naming;
using Maki.Core.Parsing;
using Maki.Core.Reading;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Chapter rows built from the files on disk for what no source lists, so a series that is all on
/// disk can still be read, marked read and finished. Runs at the end of every link pass
/// (<see cref="CbzLinkService"/>), after the linker has placed what it could.
/// <para>
/// A chapter file gets a row under its parsed number. A volume file gets one row per chapter its
/// page names mark, or, without markers and only while no source lists any chapter of the series,
/// one <see cref="Chapter.IsVolumeEntry"/> per volume it spans. A row is never built for a number or
/// volume the series already has: the linker declined that file for a reason. One-shots (no number
/// in the name) and chapter 0 are left to the linker as before.
/// </para>
/// <para>
/// Numbered rows need nothing more when a source lists them later: sync matches by number and
/// language and adds its link to the same row. A volume entry is retired by
/// <see cref="RetireVolumeEntriesAsync"/> once the linker gives its file to numbered chapters.
/// </para>
/// </summary>
public class ChapterFromFilesService(
    MakiDbContext db, ReadingProgressService progress, IAppSettings settings, ILogger<ChapterFromFilesService> logger)
{
    public sealed record FileOnDisk(int FileId, string Path, ParsedReleaseFile Parsed);

    /// <summary>
    /// Adds a row for every file in <paramref name="files"/> that no chapter in
    /// <paramref name="chapters"/> points at, to the context and to the list. The caller saves.
    /// Returns how many files gained a chapter.
    /// </summary>
    public async Task<int> BuildAsync(
        Series series, List<Chapter> chapters, IReadOnlyCollection<FileOnDisk> files, CancellationToken ct)
    {
        var linked = chapters.Where(c => c.ChapterFileId != null).Select(c => c.ChapterFileId!.Value).ToHashSet();
        var unlinked = files.Where(f => f.Parsed.IsRecognized && !linked.Contains(f.FileId)).ToList();
        if (unlinked.Count == 0)
        {
            return 0;
        }

        var sourceListed = await db.ChapterSourceLinks.AnyAsync(l => l.Chapter!.SeriesId == series.Id, ct);
        var skipSpecials = await settings.GetAsync(SettingKeys.MonitoringUnmonitorSpecials, ct) == "true";
        var built = Plan(series, chapters, unlinked, volumeEntries: !sourceListed, VolumeChapterScanner.ScanCbz, skipSpecials);
        foreach (var chapter in built)
        {
            db.Chapters.Add(chapter);
            chapters.Add(chapter);
        }

        if (built.Count > 0)
        {
            logger.LogInformation("Built {Count} chapter(s) of '{Title}' from files no source lists",
                built.Count, series.Title);
        }

        return built.Select(c => c.ChapterFileId).Distinct().Count();
    }

    /// <param name="volumeEntries">Whether a volume file with no chapter markers may become a volume entry.</param>
    internal static List<Chapter> Plan(
        Series series, IReadOnlyList<Chapter> chapters, IEnumerable<FileOnDisk> unlinked, bool volumeEntries,
        Func<string, IReadOnlyList<decimal>> markers, bool skipSpecials)
    {
        var seriesLanguages = ChapterFileLanguage.SeriesLanguages(chapters);
        var built = new List<Chapter>();
        IEnumerable<Chapter> All() => chapters.Concat(built);

        foreach (var file in unlinked.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            // A name that could be several of the series' languages names none of them.
            var language = ChapterFileLanguage.FromName(file.Path, seriesLanguages) switch
            {
                null => seriesLanguages.FirstOrDefault() ?? FileNameBuilder.DefaultLanguage,
                { Count: 1 } one => one.First(),
                _ => null
            };
            if (language is null)
            {
                continue;
            }

            var parsed = file.Parsed;
            var volume = parsed.VolumeEnd is null ? parsed.Volume : null;
            List<decimal> numbers = parsed.IsChapter ? [parsed.Number!.Value] : [.. markers(file.Path)];
            var fresh = numbers
                .Where(n => n > 0 && !All().Any(c => c.Number == n && SourceLanguages.Same(ChapterFileLanguage.Of(c), language)))
                .ToList();
            foreach (var number in fresh)
            {
                built.Add(new Chapter
                {
                    SeriesId = series.Id,
                    Number = number,
                    NumberRaw = number.ToString("0.###", CultureInfo.InvariantCulture),
                    Volume = volume,
                    Language = language,
                    Wanted = Chapter.WantedUnder(series.MonitorNewItems, number, skipSpecials),
                    ChapterFileId = file.FileId
                });
            }

            if (!parsed.IsVolume || numbers.Count > 0 || !volumeEntries)
            {
                continue;
            }

            for (var v = parsed.Volume!.Value; v <= (parsed.VolumeEnd ?? parsed.Volume.Value); v++)
            {
                if (All().Any(c => Chapter.IsVolumeEntry(c) && c.Volume == v && SourceLanguages.Same(ChapterFileLanguage.Of(c), language)))
                {
                    continue;
                }

                built.Add(new Chapter
                {
                    SeriesId = series.Id,
                    Volume = v,
                    Language = language,
                    // Nothing can download a volume entry, so it never counts as missing; while it
                    // has its file, the file alone counts it toward the total.
                    Wanted = false,
                    ChapterFileId = file.FileId
                });
            }
        }

        return built;
    }

    /// <summary>
    /// Removes every volume entry whose file now also backs numbered chapters, moving each reader's
    /// progress onto them: a finished volume marks all of them read (as a bulk mark, with the high
    /// water mark raised silently, since the read was already counted once), a volume part read
    /// keeps its place on the first of them. Saves.
    /// </summary>
    public async Task<int> RetireVolumeEntriesAsync(Series series, List<Chapter> chapters, CancellationToken ct)
    {
        var retiring = chapters
            .Where(c => Chapter.IsVolumeEntry(c) && c.ChapterFileId is { } fileId &&
                        chapters.Any(o => o.Number != null && o.ChapterFileId == fileId))
            .ToList();
        if (retiring.Count == 0)
        {
            return 0;
        }

        // Progress can only move onto rows that have ids.
        await db.SaveChangesAsync(ct);
        var rows = await db.ChapterProgress.IgnoreQueryFilters()
            .Where(p => p.SeriesId == series.Id)
            .ToListAsync(ct);
        var bookmarks = await db.ReaderBookmarks.IgnoreQueryFilters()
            .Where(b => b.SeriesId == series.Id)
            .ToListAsync(ct);
        var silentMarks = new Dictionary<int, (decimal Chapter, int Volume)>();

        foreach (var entry in retiring)
        {
            var takers = ChapterOrder.Sort(
                chapters.Where(c => c.Number != null && c.ChapterFileId == entry.ChapterFileId),
                c => c.Number, c => c.Volume, c => c.Id);
            foreach (var row in rows.Where(p => p.ChapterId == entry.Id).ToList())
            {
                var free = takers.Where(t => !rows.Any(p => p.UserId == row.UserId && p.ChapterId == t.Id)).ToList();
                if (free.Count == 0 || (row.UnreadAt is not null && !row.Completed))
                {
                    db.ChapterProgress.Remove(row);
                    rows.Remove(row);
                    continue;
                }

                row.ChapterId = free[0].Id;
                if (!row.Completed)
                {
                    continue;
                }

                foreach (var taker in free.Skip(1))
                {
                    var copy = new ChapterProgress
                    {
                        UserId = row.UserId,
                        SeriesId = series.Id,
                        ChapterId = taker.Id,
                        Completed = true,
                        Watched = row.Watched,
                        BulkMarked = true,
                        StartedAt = row.StartedAt,
                        UpdatedAt = row.UpdatedAt,
                        CompletedAt = row.CompletedAt
                    };
                    db.ChapterProgress.Add(copy);
                    rows.Add(copy);
                }

                var highest = (takers.Max(t => t.Number!.Value), entry.Volume!.Value);
                silentMarks[row.UserId] = silentMarks.TryGetValue(row.UserId, out var mark) && mark.Chapter >= highest.Item1
                    ? mark
                    : highest;
            }

            foreach (var bookmark in bookmarks.Where(b => b.ChapterId == entry.Id).ToList())
            {
                if (takers.Count > 0 &&
                    !bookmarks.Any(b => b.ChapterId == takers[0].Id && b.UserId == bookmark.UserId && b.PageIndex == bookmark.PageIndex))
                {
                    bookmark.ChapterId = takers[0].Id;
                }
                else
                {
                    db.ReaderBookmarks.Remove(bookmark);
                    bookmarks.Remove(bookmark);
                }
            }

            db.Chapters.Remove(entry);
            chapters.Remove(entry);
        }

        await db.SaveChangesAsync(ct);
        foreach (var (userId, (chapter, volume)) in silentMarks)
        {
            await progress.ImportSilentAsync(userId, series.Id, null, series.Title, (double)chapter, volume, ct);
        }

        logger.LogInformation("Handed {Count} volume entries of '{Title}' over to the chapters a source lists",
            retiring.Count, series.Title);
        return retiring.Count;
    }
}
