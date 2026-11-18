using System.Globalization;
using Maki.Core.Entities;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Acts on the folds the pages confirmed (<see cref="ChapterReconciler.CheckPages"/>): a source's
/// sub-chapters 10.1 and 10.2 become part of the whole chapter row 10. The source's parts become one
/// link on that row (<see cref="ChapterSourceLink.Parts"/>), a <see cref="ChapterFold"/> keeps sync
/// listing them that way, and a part row no other source lists goes, its reads carried onto the whole.
/// <para>
/// Nothing is lost on the way. A floor waits, and is tried again at the next pass, while a part row is
/// downloading, while a part has a file and the whole has none yet (the whole is downloaded first), or
/// while a part's file also backs another chapter. A part file the whole replaces goes to the recycle
/// bin, so it can be restored. Reading positions, bookmarks and page notes move by the pages of the
/// parts before them, scaled to the whole's file when it has another page count.
/// </para>
/// </summary>
public class ChapterFoldService(
    MakiDbContext db,
    ChapterFoldPreviewService previews,
    ChapterFileDeletion deletion,
    TimeProvider time,
    ILogger<ChapterFoldService> logger)
{
    public const string ConfirmedReason = "pagesMatch";

    /// <returns>How many source runs of parts were folded.</returns>
    public async Task<int> ApplyAsync(int? seriesId, CancellationToken ct)
    {
        var (floors, _) = await previews.DecideAsync(seriesId, ct);
        var folded = 0;
        foreach (var series in floors
                     .Where(f => f.Decision is { State: FoldState.Merged, Fold: true, Reason: ConfirmedReason })
                     .GroupBy(f => f.SeriesId))
        {
            try
            {
                using (await SeriesLocks.SeriesAsync(series.Key, ct))
                {
                    folded += await ApplySeriesAsync(series.Key, [.. series], ct);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not fold the chapters of series {SeriesId}", series.Key);
                db.ChangeTracker.Clear();
            }
        }

        return folded;
    }

    private async Task<int> ApplySeriesAsync(int seriesId, List<FoldFloor> floors, CancellationToken ct)
    {
        var series = await db.Series.Include(s => s.RootFolder).FirstOrDefaultAsync(s => s.Id == seriesId, ct);
        if (series is null || series.ArchivedAt is not null)
        {
            return 0;
        }

        var chapters = await db.Chapters.Where(c => c.SeriesId == seriesId).Include(c => c.SourceLinks).ToListAsync(ct);
        var existing = await db.ChapterFolds.Where(f => f.SeriesId == seriesId).ToListAsync(ct);
        var chapterIds = chapters.Select(c => (int?)c.Id).ToList();
        var active = (await db.DownloadQueue.IgnoreQueryFilters()
                .Where(q => chapterIds.Contains(q.ActiveChapterId))
                .Select(q => q.ActiveChapterId!.Value)
                .ToListAsync(ct))
            .ToHashSet();

        var folded = 0;
        foreach (var floor in floors)
        {
            foreach (var (mappingName, numbers) in floor.Decision.Parts)
            {
                if (!floor.MappingIds.TryGetValue(mappingName, out var mappingId) ||
                    existing.Any(f => f.SourceMappingId == mappingId && f.Floor == floor.Decision.Floor &&
                                      SourceLanguages.Same(f.Language, floor.Language)))
                {
                    continue;
                }

                if (await FoldAsync(series, chapters, active, floor, mappingId, numbers, ct) is { } fold)
                {
                    existing.Add(fold);
                    folded++;
                }
            }
        }

        return folded;
    }

    private async Task<ChapterFold?> FoldAsync(
        Series series, List<Chapter> chapters, HashSet<int> active, FoldFloor floor, int mappingId,
        IReadOnlyList<decimal> numbers, CancellationToken ct)
    {
        Chapter? Row(decimal number) => chapters.FirstOrDefault(c =>
            c.Number == number && SourceLanguages.Same(c.Language, floor.Language));

        var keeper = Row(floor.Decision.Floor);
        var parts = numbers.Select(Row).ToList();
        if (keeper is null || parts.Any(p => p is null))
        {
            return null;
        }

        var rows = parts.OfType<Chapter>().ToList();
        var links = rows.Select(r => r.SourceLinks.FirstOrDefault(l => l.SourceMappingId == mappingId)).ToList();
        if (links.Any(l => l is null))
        {
            return null;
        }

        var partLinks = links.OfType<ChapterSourceLink>().ToList();
        var pages = partLinks.Select(l => ChapterPageMatch.Parse(l.PageHashes)?.Count ?? l.PageCount ?? 0).ToList();
        if (pages.Any(p => p <= 0))
        {
            return null;
        }

        // A row another source lists stays a row; only this source's link leaves it.
        var leaving = rows.Where(r => r.SourceLinks.All(l => l.SourceMappingId == mappingId)).ToList();
        if (leaving.Any(r => active.Contains(r.Id)))
        {
            return null;
        }

        var files = new List<ChapterFile>();
        foreach (var fileId in leaving.Select(r => r.ChapterFileId).OfType<int>().Distinct().Where(id => id != keeper.ChapterFileId))
        {
            // The whole is downloaded first; a part file that also backs another chapter (a volume) stays.
            if (keeper.ChapterFileId is null || chapters.Any(c => c.ChapterFileId == fileId && !leaving.Contains(c)))
            {
                return null;
            }

            files.Add(await db.ChapterFiles.FirstAsync(f => f.Id == fileId, ct));
        }

        if (files.Count > 0)
        {
            var root = series.RootFolder?.Path;
            var absolute = files.Select(f => root is null ? null : Maki.Core.Paths.LibraryPaths.ResolveForDelete(root, f.RelativePath)).ToList();
            if (root is null || absolute.Any(a => a is null) || deletion.Bin.Refusal(root, absolute.OfType<string>()) is not null)
            {
                return null;
            }

            var binned = await deletion.DeleteAsync(series, files, ct, RecycleReason.RemoveChapter);
            if (binned.Failed > 0)
            {
                return null;
            }
        }

        var foldParts = rows.Select((r, i) => new ChapterFoldPart(partLinks[i].SourceChapterId, r.Number!.Value, partLinks[i].NumberRaw, pages[i]))
            .ToList();
        var fold = new ChapterFold
        {
            SeriesId = series.Id,
            SourceMappingId = mappingId,
            Language = keeper.Language,
            Floor = floor.Decision.Floor,
            PartsJson = ChapterFoldPart.Format(foldParts),
            FoldedAt = time.GetUtcNow().UtcDateTime,
        };
        db.ChapterFolds.Add(fold);

        if (keeper.SourceLinks.All(l => l.SourceMappingId != mappingId))
        {
            var first = partLinks[0];
            var hashes = partLinks.Select(l => ChapterPageMatch.Parse(l.PageHashes)).ToList();
            db.ChapterSourceLinks.Add(new ChapterSourceLink
            {
                Chapter = keeper,
                SourceMappingId = mappingId,
                SourceChapterId = first.SourceChapterId,
                NumberRaw = floor.Decision.Floor.ToString(CultureInfo.InvariantCulture),
                Volume = first.Volume,
                ReleaseDate = partLinks.Select(l => l.ReleaseDate).Min(),
                Group = first.Group,
                Url = first.Url,
                PageCount = pages.Sum(),
                PageHashes = hashes.All(h => h is not null) ? ChapterPageMatch.Format(hashes.SelectMany(h => h!)) : null,
                StripHashes = partLinks.Select(l => ChapterPageMatch.ParseStrip(l.StripHashes)).ToList() is var strips && strips.All(s => s is { Count: > 0 })
                    ? ChapterPageMatch.FormatStrip([.. strips.SelectMany(s => s!)])
                    : null,
                Parts = fold.PartsJson,
                FirstSeenAt = partLinks.Select(l => l.FirstSeenAt).Min(),
            });
        }

        db.ChapterSourceLinks.RemoveRange(partLinks);
        foreach (var row in rows)
        {
            row.SourceLinks.Remove(partLinks[rows.IndexOf(row)]);
        }

        var wholePages = keeper.ChapterFileId is { } keeperFile
            ? await db.ChapterFiles.Where(f => f.Id == keeperFile).Select(f => f.SplitPageCount ?? f.PageCount).FirstOrDefaultAsync(ct)
            : null;
        var offsets = new Dictionary<int, int>();
        var offset = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            offsets[rows[i].Id] = offset;
            offset += pages[i];
        }

        var position = new PartPositions(offsets, pages.Sum(), wholePages);
        await MoveReadsAsync(series.Id, keeper, leaving, position, ct);
        foreach (var row in leaving)
        {
            keeper.Wanted |= row.Wanted;
            keeper.ReleaseDate ??= row.ReleaseDate;
            chapters.Remove(row);
            db.Chapters.Remove(row);
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Folded {Parts} of mapping {MappingId} into chapter {Floor} of series {SeriesId}",
            string.Join(", ", numbers.Select(n => n.ToString(CultureInfo.InvariantCulture))), mappingId,
            floor.Decision.Floor, series.Id);
        return fold;
    }

    /// <summary>Where a page of a part lands in the whole: after the parts before it, scaled to the whole's file.</summary>
    private sealed record PartPositions(IReadOnlyDictionary<int, int> Offsets, int Total, int? WholePages)
    {
        public int Map(int chapterId, int page)
        {
            var at = Offsets[chapterId] + Math.Max(0, page);
            if (WholePages is { } whole && whole > 0 && whole != Total)
            {
                at = (int)Math.Round((double)at * whole / Total);
            }

            return Math.Clamp(at, 0, Math.Max(0, (WholePages ?? Total) - 1));
        }

        public int Pages => WholePages ?? Total;
    }

    /// <summary>
    /// Every reader's progress, bookmarks and notes on the leaving part rows, moved onto the whole.
    /// A reader who finished every part has finished the whole; one part in, the whole is in progress at
    /// that page. The whole's own progress wins when it is further. Seconds read add up, and so do the
    /// seconds already reported, so nothing is reported twice.
    /// </summary>
    private async Task MoveReadsAsync(int seriesId, Chapter keeper, List<Chapter> leaving, PartPositions position, CancellationToken ct)
    {
        if (leaving.Count == 0)
        {
            return;
        }

        var ids = leaving.Select(r => r.Id).ToHashSet();
        var order = leaving.OrderBy(r => position.Offsets[r.Id]).Select(r => r.Id).ToList();
        var progress = await db.ChapterProgress.IgnoreQueryFilters()
            .Where(p => p.SeriesId == seriesId && (ids.Contains(p.ChapterId) || p.ChapterId == keeper.Id))
            .ToListAsync(ct);
        foreach (var reader in progress.Where(p => ids.Contains(p.ChapterId)).GroupBy(p => p.UserId))
        {
            var rows = reader.OrderBy(p => order.IndexOf(p.ChapterId)).ToList();
            var all = order.All(id => rows.Any(p => p.ChapterId == id && p.Completed && p.UnreadAt is null));
            var current = rows.FirstOrDefault(p => !p.Completed || p.UnreadAt is not null) ?? rows[^1];
            var whole = progress.FirstOrDefault(p => p.ChapterId == keeper.Id && p.UserId == reader.Key);
            var carrier = whole ?? current;
            var at = all ? position.Pages - 1 : position.Map(current.ChapterId, current.PageIndex);
            var ahead = whole is not null && (whole.Completed || whole.PageIndex >= at) && whole.UnreadAt is null;
            var everyRow = whole is null ? rows : [.. rows, whole];

            carrier.ReadSeconds = everyRow.Sum(p => p.ReadSeconds);
            carrier.ReportedSeconds = everyRow.Sum(p => p.ReportedSeconds);
            carrier.StartedAt = everyRow.Min(p => p.StartedAt);
            carrier.UpdatedAt = everyRow.Max(p => p.UpdatedAt);
            carrier.CountedAt = everyRow.Select(p => p.CountedAt).Max();
            if (!ahead)
            {
                carrier.Completed = all;
                carrier.CompletedAt = all ? rows.Select(p => p.CompletedAt).Max() : null;
                carrier.Watched = all && rows.All(p => p.Watched);
                carrier.BulkMarked = all && rows.Any(p => p.BulkMarked);
                carrier.External = rows.All(p => p.External);
                carrier.UnreadAt = all ? null : current.UnreadAt;
                carrier.PageIndex = at;
                carrier.PageOffset = all ? 0 : current.PageOffset;
            }

            carrier.PageCount = position.Pages;
            carrier.ChapterId = keeper.Id;
            foreach (var other in rows.Where(p => !ReferenceEquals(p, carrier)))
            {
                db.ChapterProgress.Remove(other);
            }
        }

        var bookmarks = await db.ReaderBookmarks.IgnoreQueryFilters()
            .Where(b => b.SeriesId == seriesId && (ids.Contains(b.ChapterId) || b.ChapterId == keeper.Id))
            .ToListAsync(ct);
        var taken = bookmarks.Where(b => b.ChapterId == keeper.Id).Select(b => (b.UserId, b.PageIndex)).ToHashSet();
        foreach (var mark in bookmarks.Where(b => ids.Contains(b.ChapterId)).ToList())
        {
            var page = position.Map(mark.ChapterId, mark.PageIndex);
            if (!taken.Add((mark.UserId, page)))
            {
                db.ReaderBookmarks.Remove(mark);
                continue;
            }

            mark.PageIndex = page;
            mark.ChapterId = keeper.Id;
        }

        foreach (var note in await db.ChapterNotes.IgnoreQueryFilters()
                     .Where(n => n.SeriesId == seriesId && ids.Contains(n.ChapterId))
                     .ToListAsync(ct))
        {
            note.PageIndex = note.PageIndex is { } page ? position.Map(note.ChapterId, page) : null;
            note.ChapterId = keeper.Id;
        }
    }
}
