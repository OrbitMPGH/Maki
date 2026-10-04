using System.Globalization;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Http;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Refreshes a series' chapter list from its source mappings and diffs it
/// against what is already known. New chapters are inserted; existing ones
/// are matched by (Number, Language) — or title for one-shots. Volume is a
/// wildcard: sources disagree on whether chapters carry volume info, so a
/// null volume on either side still matches, and volume-aware sources
/// backfill the volume onto chapters first seen without one.
/// </summary>
public class ChapterSyncService(
    MakiDbContext db,
    SourceRegistry sourceRegistry,
    DownloadQueueService queue,
    SourceAvailability sourceAvailability,
    SourceChapterListCache chapterLists,
    IAppSettings appSettings,
    ILogger<ChapterSyncService> logger,
    AnimeResumePendingService? animeResumePending = null)
{
    /// <returns>Ids of newly discovered chapters.</returns>
    public Task<List<int>> SyncSeriesAsync(int seriesId, CancellationToken ct = default) =>
        SyncSeriesAsync(seriesId, mappingIds: null, ct);

    /// <summary>
    /// Refreshes only the selected mappings. Used to populate missing cleanup snapshots without
    /// re-requesting every source that already has one.
    /// </summary>
    public Task<List<int>> SyncMappingsAsync(
        int seriesId, IReadOnlyCollection<int> mappingIds, CancellationToken ct = default) =>
        SyncSeriesAsync(seriesId, mappingIds.ToHashSet(), ct);

    private async Task<List<int>> SyncSeriesAsync(
        int seriesId, HashSet<int>? mappingIds, CancellationToken ct)
    {
        var series = await db.Series
            .Include(s => s.SourceMappings)
            .ThenInclude(m => m.ChapterLinks)
            .FirstOrDefaultAsync(s => s.Id == seriesId, ct)
            ?? throw new InvalidOperationException($"Series {seriesId} not found");

        var existing = await db.Chapters
            .Where(c => c.SeriesId == seriesId)
            .Include(c => c.SourceLinks)
            .ToListAsync(ct);
        var reads = new ReadRows(db, seriesId);
        await MergeDuplicatesAsync(existing, reads, ct);
        // Read once per sync, not once per discovered chapter: this is the only thing that decides
        // whether a newly listed special is wanted, and a Smart series can't say so via its mode.
        var skipSpecials = await appSettings.GetAsync(SettingKeys.MonitoringUnmonitorSpecials, ct) == "true";
        var newChapters = new List<Chapter>();
        var numbersBySource = new Dictionary<string, IReadOnlyCollection<decimal?>>();
        var promotedBesideExisting = false;

        // MangaBaka has no MangaDex ids, so the uuid can only come from a linked
        // source mapping; it feeds the series web links.
        series.MangaDexUuid ??= series.SourceMappings
            .FirstOrDefault(m => m.SourceName == "mangadex")?.SourceSeriesId;

        // A globally switched-off source counts as disabled here without its per-series
        // mappings being touched, so re-enabling it brings back the user's own layout.
        var disabledSources = await sourceAvailability.DisabledAsync(ct);
        var liveMappings = series.SourceMappings
            .Where(m => m.Enabled && !disabledSources.Contains(m.SourceName, StringComparer.OrdinalIgnoreCase))
            .Where(m => mappingIds is null || mappingIds.Contains(m.Id))
            .ToList();

        foreach (var mapping in liveMappings)
        {
            var source = sourceRegistry.Find(mapping.SourceName);
            if (source is null)
            {
                logger.LogWarning("Mapping {Id} references unknown source {Source}", mapping.Id, mapping.SourceName);
                continue;
            }

            try
            {
                // Uncached on purpose: a refresh has to see the site as it is now. Seeding the
                // shared cache with the result is what keeps the enqueues that a monitored refresh
                // fires immediately afterwards from resolving against an older listing, which would
                // report the chapter this pass just discovered as "not listed".
                var sourceChapters = (await source.ListChaptersAsync(mapping.SourceSeriesId, mapping.LanguageFilter, ct))
                    .Select(ChapterIdentity.Labelled)
                    .ToList();
                if (sourceChapters.Count == 0 && mapping.ChapterLinks.Count > 0)
                {
                    // A challenge page or a changed layout parses to nothing. Replacing the snapshot
                    // with that would drop every link and cache the empty listing, so downloads then
                    // fail as "not listed". Plain English like the rest of LastError.
                    throw new InvalidOperationException(
                        "The source listed no chapters, so the previous chapter list was kept");
                }

                chapterLists.Store(source, mapping.SourceSeriesId, mapping.LanguageFilter, sourceChapters);
                numbersBySource[mapping.SourceName] = sourceChapters.Select(sc => sc.Number).ToList();
                var snapshotLinks = new Dictionary<Chapter, SourceChapter>();

                foreach (var sc in sourceChapters)
                {
                    var match = existing.FirstOrDefault(c => ChapterIdentity.Matches(c, sc))
                                ?? UntitledOneShot(existing, sc, mapping);
                    if (PromotableOneShot(existing, sc) is { } unnumbered &&
                        (match is null || await reads.CanMergeAsync(match, unnumbered, ct)))
                    {
                        // Stored as a one-shot titled by its label back when the parser could not
                        // read that label ("Episode 12"). Numbering it in place keeps its file and
                        // stops a second row being created, and downloaded, beside it. When another
                        // source already has that number, the two rows are merged below, unless
                        // both hold a file or both hold one reader's progress.
                        unnumbered.Number = sc.Number;
                        unnumbered.NumberRaw = sc.NumberRaw;
                        unnumbered.IsOneShot = false;
                        unnumbered.Title = sc.Title;
                        promotedBesideExisting |= match is not null;
                        match ??= unnumbered;
                    }

                    if (match is not null)
                    {
                        // Enrich rather than duplicate: a volume-aware source fills in
                        // what a volume-less source couldn't provide.
                        match.Volume ??= sc.Volume;
                        match.Title ??= sc.Title;
                        match.ReleaseDate ??= sc.ReleaseDate;
                    }
                    else
                    {
                        match = new Chapter
                        {
                            SeriesId = seriesId,
                            Number = sc.Number,
                            NumberRaw = sc.NumberRaw,
                            Volume = sc.Volume,
                            Title = sc.Title,
                            IsOneShot = sc.Number is null,
                            Language = sc.Language,
                            ReleaseDate = sc.ReleaseDate,
                            Wanted = Chapter.WantedUnder(series.MonitorNewItems, sc.Number, skipSpecials)
                        };
                        db.Chapters.Add(match);
                        existing.Add(match);
                        newChapters.Add(match);
                    }

                    // Some sites publish several releases for the same number and language. The
                    // chapter table deliberately merges those, and its snapshot needs only one
                    // representative entry from this mapping too.
                    snapshotLinks.TryAdd(match, sc);
                }

                var fileGroups = ReplaceSnapshot(mapping, snapshotLinks, ChapterFileQualityService.SiteGroup(source));
                await BackfillFileGroupsAsync(mapping.SourceName, fileGroups, ct);

                mapping.LastRefresh = DateTime.UtcNow;
                mapping.ChapterSnapshotAt = mapping.LastRefresh;
                mapping.LastError = null;
            }
            catch (Exception ex) when (RateLimitDetector.IsRateLimit(ex, out var retryAfter))
            {
                // The source is throttling us, and it doesn't care which subsystem is asking — so
                // back that source's download queue cooldown off too rather than letting it walk
                // into the same 429s seconds later. The sync itself still just records the error
                // and moves on.
                var until = queue.EnterRateLimitCooldown(mapping.SourceName, retryAfter);
                logger.LogWarning(
                    "Rate limited by {Source} during chapter sync of series {SeriesId}; " +
                    "pausing its downloads until {Until:o}",
                    mapping.SourceName, seriesId, until);
                // Not yet keyed, like the rest of LastError; see the property's doc comment. Just
                // the wording (no em dash) and an explicit culture for the time, consistent with
                // every other formatted time/number in the app.
                mapping.LastError =
                    $"Rate limited, downloads paused until {until.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)}";
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Chapter sync failed for {Source} mapping of series {SeriesId}",
                    mapping.SourceName, seriesId);
                mapping.LastError = ex.Message;
            }
        }

        if (promotedBesideExisting)
        {
            await MergeDuplicatesAsync(existing, reads, ct);
        }

        // Flag (or clear) cross-source numbering clashes. A clash is always
        // recorded; clearing requires every enabled mapping to have fetched this
        // run, so one temporarily failing source doesn't wipe a real flag.
        // A targeted snapshot fill sees only part of the source layout, so it cannot safely create
        // or clear a cross-source numbering verdict.
        if (mappingIds is null)
        {
            var clash = NumberingClashDetector.Detect(numbersBySource);
            var value = clash is null ? null : $"{clash.SubChapterSource}|{clash.WholeChapterSource}";
            var allFetched = numbersBySource.Count == liveMappings.Count;
            if (value is not null || allFetched)
            {
                if (value != series.NumberingClash)
                {
                    logger.LogInformation("Numbering clash on series {SeriesId}: {Value}", seriesId, value ?? "cleared");
                }

                series.NumberingClash = value;
            }
        }

        await db.SaveChangesAsync(ct);
        if (newChapters.Count > 0 && animeResumePending is not null)
        {
            await animeResumePending.ApplyAsync(seriesId, ct);
        }

        return newChapters.Select(c => c.Id).ToList();
    }

    /// <returns>The group each linked chapter's file should carry, by ChapterFile id.</returns>
    private Dictionary<int, string> ReplaceSnapshot(
        SourceMapping mapping,
        IReadOnlyDictionary<Chapter, SourceChapter> snapshot,
        string? groupFallback)
    {
        var fileGroups = new Dictionary<int, string>();
        var current = mapping.ChapterLinks.ToDictionary(l => l.ChapterId);
        var retainedChapterIds = snapshot.Keys.Where(c => c.Id != 0).Select(c => c.Id).ToHashSet();

        foreach (var stale in mapping.ChapterLinks
                     .Where(l => l.ChapterId != 0 && !retainedChapterIds.Contains(l.ChapterId))
                     .ToList())
        {
            db.ChapterSourceLinks.Remove(stale);
            mapping.ChapterLinks.Remove(stale);
        }

        foreach (var (chapter, sourceChapter) in snapshot)
        {
            if (chapter.Id == 0 || !current.TryGetValue(chapter.Id, out var link))
            {
                link = new ChapterSourceLink { Chapter = chapter, SourceMapping = mapping };
                db.ChapterSourceLinks.Add(link);
            }

            link.SourceChapterId = sourceChapter.SourceChapterId;
            link.NumberRaw = sourceChapter.NumberRaw;
            link.Volume = sourceChapter.Volume;
            link.Title = sourceChapter.Title;
            link.ReleaseDate = sourceChapter.ReleaseDate;
            link.Group = sourceChapter.Group ?? groupFallback;
            if (chapter.ChapterFileId is { } fileId && link.Group is { } group)
            {
                fileGroups.TryAdd(fileId, group);
            }
        }

        return fileGroups;
    }

    /// <summary>
    /// A file downloaded before its link recorded a group has none of its own. Fills it in from
    /// the link, for files that came from this same source only.
    /// </summary>
    private async Task BackfillFileGroupsAsync(
        string sourceName, Dictionary<int, string> fileGroups, CancellationToken ct)
    {
        if (fileGroups.Count == 0)
        {
            return;
        }

        var ids = fileGroups.Keys.ToList();
        var files = await db.ChapterFiles
            .Where(f => ids.Contains(f.Id) && f.SourceName == sourceName && f.Group == null)
            .ToListAsync(ct);
        foreach (var file in files.Where(f => f.Group is null))
        {
            file.Group = fileGroups[file.Id];
        }
    }

    /// <summary>
    /// Heals duplicates created before volume became a wildcard in matching:
    /// the same chapter synced once with a volume ("Vol.4 Ch.27") and once
    /// without ("Ch.27"). Keeps the richest copy and deletes the rest.
    /// </summary>
    internal static Chapter? PromotableOneShot(List<Chapter> existing, SourceChapter sc) =>
        sc.Number is null || string.IsNullOrWhiteSpace(sc.NumberRaw)
            ? null
            : existing.FirstOrDefault(c =>
                c.Number is null &&
                c.IsOneShot &&
                c.Language == sc.Language &&
                string.Equals(c.Title?.Trim(), sc.NumberRaw.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A one-shot stored before untitled chapters took their label as a title. Adopting it keeps its
    /// file rather than downloading the same chapter again under the label. One already linked to a
    /// different listing of this mapping belongs to that listing, not this one.
    /// </summary>
    private static Chapter? UntitledOneShot(List<Chapter> existing, SourceChapter sc, SourceMapping mapping)
    {
        if (sc.Number is not null || sc.Title is null ||
            !string.Equals(sc.Title, sc.NumberRaw?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var untitled = existing
            .Where(c => c.Number is null && c.IsOneShot && c.Title is null && c.Language == sc.Language)
            .ToList();
        string? LinkedId(Chapter c) =>
            c.SourceLinks.FirstOrDefault(l => l.SourceMappingId == mapping.Id)?.SourceChapterId;

        return untitled.FirstOrDefault(c => LinkedId(c) == sc.SourceChapterId)
               ?? untitled.FirstOrDefault(c => LinkedId(c) is null);
    }

    private async Task MergeDuplicatesAsync(List<Chapter> existing, ReadRows reads, CancellationToken ct)
    {
        var groups = existing
            .Where(c => c.Number is not null)
            .GroupBy(c => (c.Number, c.Language))
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in groups)
        {
            // Two different explicit volumes means per-volume numbering, not a duplicate.
            if (group.Select(c => c.Volume).OfType<int>().Distinct().Count() > 1)
            {
                continue;
            }

            // Persisted rows before new ones: progress can only move onto a row that has an id.
            var keeper = group
                .OrderByDescending(c => c.ChapterFileId != null)
                .ThenByDescending(c => c.Id != 0)
                .ThenByDescending(c => c.Volume != null)
                .ThenBy(c => c.Id)
                .First();

            var merged = 0;
            foreach (var dup in group.Where(c => !ReferenceEquals(c, keeper)))
            {
                // The keeper holds one file and one progress row per reader, so a duplicate with a
                // second file or a second read of its own stays rather than losing it.
                if (!await reads.CanMergeAsync(keeper, dup, ct))
                {
                    logger.LogWarning(
                        "Kept duplicate row {DupId} of chapter {Number} in series {SeriesId}: its file or progress cannot move to row {KeeperId}",
                        dup.Id, keeper.Number, keeper.SeriesId, keeper.Id);
                    continue;
                }

                keeper.Volume ??= dup.Volume;
                keeper.Title ??= dup.Title;
                keeper.ReleaseDate ??= dup.ReleaseDate;
                keeper.ChapterFileId ??= dup.ChapterFileId;
                keeper.Wanted |= dup.Wanted;
                await reads.MoveAsync(dup, keeper, ct);

                foreach (var link in dup.SourceLinks.ToList())
                {
                    var existingLink = keeper.SourceLinks
                        .FirstOrDefault(l => l.SourceMappingId == link.SourceMappingId);
                    if (existingLink is null)
                    {
                        var replacement = new ChapterSourceLink
                        {
                            Chapter = keeper,
                            SourceMappingId = link.SourceMappingId,
                            SourceMapping = link.SourceMapping,
                            SourceChapterId = link.SourceChapterId,
                            NumberRaw = link.NumberRaw,
                            Volume = link.Volume,
                            Title = link.Title,
                            ReleaseDate = link.ReleaseDate,
                            Group = link.Group
                        };
                        db.ChapterSourceLinks.Add(replacement);
                    }

                    db.ChapterSourceLinks.Remove(link);
                }

                existing.Remove(dup);
                db.Chapters.Remove(dup);
                merged++;
            }

            if (merged > 0)
            {
                logger.LogInformation("Merged {Count} duplicate row(s) of chapter {Number} in series {SeriesId}",
                    merged, keeper.Number, keeper.SeriesId);
            }
        }
    }

    /// <summary>
    /// Every reader's progress and bookmarks on the series, loaded only once a merge needs them.
    /// Unfiltered, since a sync started from one user's request must not drop another user's reads.
    /// </summary>
    private sealed class ReadRows(MakiDbContext db, int seriesId)
    {
        private List<ChapterProgress>? _progress;
        private List<ReaderBookmark>? _bookmarks;

        /// <summary>Whether one row can absorb the other without dropping a file or a reader's progress.</summary>
        public async Task<bool> CanMergeAsync(Chapter a, Chapter b, CancellationToken ct)
        {
            if (a.ChapterFileId is { } fileA && b.ChapterFileId is { } fileB && fileA != fileB)
            {
                return false;
            }

            if (a.Id == 0 || b.Id == 0)
            {
                return true;
            }

            var progress = await ProgressAsync(ct);
            var readersOfA = progress.Where(p => p.ChapterId == a.Id).Select(p => p.UserId).ToHashSet();
            return !progress.Any(p => p.ChapterId == b.Id && readersOfA.Contains(p.UserId));
        }

        public async Task MoveAsync(Chapter from, Chapter to, CancellationToken ct)
        {
            if (from.Id == 0 || to.Id == 0)
            {
                return;
            }

            foreach (var row in (await ProgressAsync(ct)).Where(p => p.ChapterId == from.Id))
            {
                row.ChapterId = to.Id;
            }

            var bookmarks = _bookmarks ??= await db.ReaderBookmarks.IgnoreQueryFilters()
                .Where(b => b.SeriesId == seriesId)
                .ToListAsync(ct);
            foreach (var mark in bookmarks.Where(b => b.ChapterId == from.Id).ToList())
            {
                // The same page bookmarked on both rows: the keeper's copy already covers it.
                if (!bookmarks.Any(b => b.ChapterId == to.Id && b.UserId == mark.UserId && b.PageIndex == mark.PageIndex))
                {
                    mark.ChapterId = to.Id;
                }
            }
        }

        private async Task<List<ChapterProgress>> ProgressAsync(CancellationToken ct) =>
            _progress ??= await db.ChapterProgress.IgnoreQueryFilters()
                .Where(p => p.SeriesId == seriesId)
                .ToListAsync(ct);
    }
}
