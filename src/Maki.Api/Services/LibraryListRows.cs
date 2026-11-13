using Maki.Core.Configuration;
using System.Linq.Expressions;
using Maki.Api.Dtos;
using Maki.Api.Jobs;
using Maki.Core.Entities;
using Maki.Core.Reading;
using Maki.Data;
using Maki.Metadata.MangaBaka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;

namespace Maki.Api.Services;

/// <summary>
/// The Library list's rows, built one way for every caller: <c>GET series</c>, <c>GET series/summary</c>
/// and the custom rails that evaluate a saved Library filter on the server. Every per-series
/// aggregate is one grouped query, never one per series.
/// </summary>
internal static class LibraryListRows
{
    /// <param name="ids">The series to build, or null for every series the caller can see.</param>
    public static async Task<List<SeriesDto>> BuildAsync(
        MakiDbContext db, SourceAvailability sourceAvailability, MangaBakaLocalStore mangaBakaStore,
        IAppSettings appSettings, string? titleLanguage, ILogger logger, int[]? ids, CancellationToken ct)
    {
        var series = await ForSeries(db.Series.AsNoTracking(), ids, s => s.Id).OrderBy(s => s.SortTitle).ToListAsync(ct);
        var chapterCounts = await ChapterTalliesAsync(ForSeries(db.Chapters, ids, c => c.SeriesId), ct);

        // Active download work per series, so cards can show "queued"/"downloading" at a glance.
        var queueCounts = await ForSeries(db.DownloadQueue, ids, q => q.SeriesId)
            .Where(q => q.Status != QueueStatus.Completed && q.Status != QueueStatus.Failed &&
                        q.Status != QueueStatus.Cancelled && q.Status != QueueStatus.Waiting)
            .GroupBy(q => q.SeriesId)
            .Select(g => new
            {
                SeriesId = g.Key,
                Queued = g.Count(q => q.Status == QueueStatus.Queued || q.Status == QueueStatus.RateLimited),
                Downloading = g.Count(q => q.Status != QueueStatus.Queued && q.Status != QueueStatus.RateLimited),
            })
            .ToDictionaryAsync(x => x.SeriesId, ct);

        var readCounts = await ReadChapterCountsBySeriesAsync(db, ids, ct);
        var readSpecialCounts = await ReadSpecialCountsBySeriesAsync(db, ids, ct);
        var unmonitorSpecials = await UnmonitorSpecialsAsync(appSettings, ct);

        // Flat join-table read; the SeriesTag query filter keeps it to series the caller can see.
        var tagIdsBySeries = (await ForSeries(db.SeriesTags, ids, x => x.SeriesId)
                .AsNoTracking()
                .ToListAsync(ct))
            .GroupBy(x => x.SeriesId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.TagId).ToList());

        // One query for the caller's own per-series state — the query filter narrows it to their
        // rows, so a shared library shows each reader their own score and their own notification
        // mode with no per-series lookup. Unfiltered on Rating now that a row can exist for the
        // mode alone: filtering on it would report every muted-but-unrated series as Default.
        var userStates = await ForSeries(db.UserSeriesStates, ids, x => x.SeriesId)
            .Select(x => new
            {
                x.SeriesId, x.Rating, x.ProvisionalRating, x.ProvisionalAtChapter, x.NotificationMode, x.Status,
                x.StatusAuto,
            })
            .ToDictionaryAsync(x => x.SeriesId, x => x, ct);
        var finishedIds = (await ForSeries(db.ReadingStates, ids, r => r.SeriesId)
            .Where(r => r.Finished)
            .Select(r => r.SeriesId)
            .ToListAsync(ct)).ToHashSet();

        // Which sources each series is linked to, and which of those actually run. Two flat reads
        // grouped in memory rather than Include(s => s.SourceMappings) on the materialized list
        // above: the same shape as the tag read, and one query instead of one per series.
        var disabledSources = await sourceAvailability.DisabledAsync(ct);
        var mappingsBySeries = (await ForSeries(db.SourceMappings, ids, m => m.SeriesId)
                .Select(m => new { m.SeriesId, m.SourceName, m.Enabled })
                .ToListAsync(ct))
            .GroupBy(m => m.SeriesId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Grouped in SQL to one row per (series, source) before crossing the wire. The ChapterFile
        // query filter already scopes this to visible series, so no explicit Series check here.
        var fileSourcesBySeries = (await ForSeries(db.ChapterFiles, ids, f => f.SeriesId)
                .Where(f => f.SourceName != "")
                .GroupBy(f => new { f.SeriesId, f.SourceName })
                .Select(g => g.Key)
                .ToListAsync(ct))
            .GroupBy(f => f.SeriesId)
            .ToDictionary(g => g.Key, g => g.Select(f => f.SourceName).Order().ToList());

        // One grouped read each. Only genuine reads date a series: watched ticks, bulk mark-reads
        // and Kavita syncs say nothing about when anyone read, mark-unread tombstones are the
        // opposite of a read, and a fully incognito series keeps no reading history. The series
        // join also keeps the aggregate to series the caller can see.
        var lastReadBySeries = await ForSeries(db.ChapterProgress, ids, p => p.SeriesId)
            .Where(p => !p.Watched && !p.BulkMarked && p.UnreadAt == null)
            .Where(p => db.Series.Any(s => s.Id == p.SeriesId && s.Incognito != IncognitoMode.Full))
            .OwnedByScopeUser(db)
            .GroupBy(p => p.SeriesId)
            .Select(g => new { SeriesId = g.Key, At = g.Max(p => p.UpdatedAt) })
            .ToDictionaryAsync(x => x.SeriesId, x => x.At, ct);

        var lastDownloadBySeries = await ForSeries(db.ChapterFiles, ids, f => f.SeriesId)
            .GroupBy(f => f.SeriesId)
            .Select(g => new { SeriesId = g.Key, At = g.Max(f => f.DateAdded) })
            .ToDictionaryAsync(x => x.SeriesId, x => x.At, ct);

        var latestReleaseBySeries = await ForSeries(db.Chapters, ids, c => c.SeriesId)
            .Where(c => c.ReleaseDate != null)
            .GroupBy(c => c.SeriesId)
            .Select(g => new { SeriesId = g.Key, At = g.Max(c => c.ReleaseDate) })
            .ToDictionaryAsync(x => x.SeriesId, x => x.At, ct);

        var sizeBySeries = await ForSeries(db.ChapterFiles, ids, f => f.SeriesId)
            .GroupBy(f => f.SeriesId)
            .Select(g => new { SeriesId = g.Key, Bytes = g.Sum(f => f.Size) })
            .ToDictionaryAsync(x => x.SeriesId, x => x.Bytes, ct);

        // The same set the monitored refresh picks from, so the table's "Next check" never names a
        // time for a series the job will pass over.
        var refreshable = (await ForSeries(RefreshMonitoredSeriesJob.Refreshable(db, disabledSources), ids, s => s.Id)
            .Select(s => s.Id)
            .ToListAsync(ct)).ToHashSet();

        var newChapterCounts = await NewChapterCountsAsync(db, ids, ct);

        var communityRatings = await CommunityRatingsAsync(mangaBakaStore, logger, series, ct);

        return [.. series.Select(s =>
        {
            chapterCounts.TryGetValue(s.Id, out var counts);
            queueCounts.TryGetValue(s.Id, out var queue);
            // Nullable on purpose: absent means nothing has ever been read, which the UI hides
            // rather than drawing an empty "0 read" bar. `out var` would type this as int and
            // silently turn every untouched series into a reported zero.
            int? readCount = readCounts.TryGetValue(s.Id, out var read) ? read : null;
            var mappings = mappingsBySeries.GetValueOrDefault(s.Id) ?? [];
            var userState = userStates.GetValueOrDefault(s.Id);
            var progress = ProgressTotals(s, counts, readCount, readSpecialCounts.GetValueOrDefault(s.Id), unmonitorSpecials);
            return SeriesDto.FromEntity(
                s, counts?.Wanted ?? 0, counts?.WithFile ?? 0, counts?.Known ?? 0,
                queue?.Queued ?? 0, queue?.Downloading ?? 0, readCount,
                tagIdsBySeries.GetValueOrDefault(s.Id) ?? [],
                userState?.Rating,
                notificationMode: userState?.NotificationMode ?? SeriesNotificationMode.Default,
                titleLanguage: titleLanguage) with
            {
                // The list is the heaviest response there is; nothing that reads it needs the links
                // or the alt titles' languages, which the detail endpoint still sends.
                AltTitles = null,
                AltTitleNames = [.. s.AltTitles.Select(t => t.Title)],
                Links = null,
                RemovedChapterCount = counts?.Removed ?? 0,
                ProgressTotal = progress.Total,
                ProgressHave = progress.Have,
                ProgressRead = progress.Read,
                SpecialsCounted = progress.SpecialsCounted,
                ListedSpecials = progress.ListedSpecials,
                ProvisionalRating = userState?.ProvisionalRating,
                ProvisionalAtChapter = userState?.ProvisionalAtChapter,
                Finished = finishedIds.Contains(s.Id),
                ReadingStatus = (userState?.Status ?? ReadingStatus.PlanToRead).ToString(),
                ReadingStatusAuto = userState?.StatusAuto ?? false,
                Sources = [.. mappings.Select(m => m.SourceName).Distinct().Order()],
                EnabledSources =
                [
                    .. mappings
                        .Where(m => m.Enabled && !disabledSources.Contains(m.SourceName, StringComparer.OrdinalIgnoreCase))
                        .Select(m => m.SourceName)
                        .Distinct()
                        .Order(),
                ],
                FileSources = fileSourcesBySeries.GetValueOrDefault(s.Id) ?? [],
                LastReadAt = lastReadBySeries.TryGetValue(s.Id, out var lastRead) ? lastRead : null,
                LastDownloadedAt = lastDownloadBySeries.TryGetValue(s.Id, out var lastDownload) ? lastDownload : null,
                NewChapterCount = newChapterCounts.GetValueOrDefault(s.Id),
                LatestChapterAt = latestReleaseBySeries.GetValueOrDefault(s.Id),
                CommunityRating = s.MangaBakaId is int bakaId && communityRatings.TryGetValue(bakaId, out var score)
                    ? Math.Round(score / 10.0, 1)
                    : null,
                SizeOnDisk = sizeBySeries.TryGetValue(s.Id, out var bytes) ? bytes : null,
                // A series that is due (no plan yet) carries a time already past, so the client reads
                // it as due and two builds of the same row stay equal.
                NextCheckAt = refreshable.Contains(s.Id) ? s.NextCheckAt ?? s.LastCheckAt ?? s.Added : null,
            };
        })];
    }

    /// <summary>Narrows a list query to <paramref name="ids"/>, or leaves it whole when that is null.</summary>
    private static IQueryable<T> ForSeries<T>(IQueryable<T> query, int[]? ids, Expression<Func<T, int>> seriesId) =>
        ids is null ? query : Where(query, seriesId, id => ids.Contains(id));

    private static IQueryable<T> ForSeries<T>(IQueryable<T> query, int[]? ids, Expression<Func<T, int?>> seriesId) =>
        ids is null ? query : Where(query, seriesId, id => id != null && ids.Contains(id.Value));

    private static IQueryable<T> Where<T, TKey>(
        IQueryable<T> query, Expression<Func<T, TKey>> seriesId, Expression<Func<TKey, bool>> contains)
    {
        var body = Microsoft.EntityFrameworkCore.Query.ReplacingExpressionVisitor.Replace(
            contains.Parameters[0], seriesId.Body, contains.Body);
        return query.Where(Expression.Lambda<Func<T, bool>>(body, seriesId.Parameters));
    }

    /// <summary>
    /// MangaBaka scores (0–100) keyed by MangaBaka id, read live from the dump rather than stored on
    /// the series so they follow each nightly refresh. Empty when the dump is off or unreadable: the
    /// library has to load without it.
    /// </summary>
    private static async Task<IReadOnlyDictionary<long, double>> CommunityRatingsAsync(
        MangaBakaLocalStore mangaBakaStore, ILogger logger, List<Series> series, CancellationToken ct)
    {
        var ids = series.Where(s => s.MangaBakaId != null).Select(s => (long)s.MangaBakaId!.Value).ToHashSet();
        if (ids.Count == 0 || !await mangaBakaStore.IsAvailableAsync(ct))
        {
            return new Dictionary<long, double>();
        }

        try
        {
            return await mangaBakaStore.GetRatingsAsync(ids, ct);
        }
        catch (SqliteException ex)
        {
            logger.LogWarning(ex, "Could not read MangaBaka ratings for the library list");
            return new Dictionary<long, double>();
        }
    }

    /// <summary>
    /// Per series, the caller's <see cref="NewChapterBatch"/> count, the same "N new" Home's
    /// Recently added rail shows. Series with nothing new are absent.
    /// <para>
    /// Decides unread for the batch chapters only, by the rule
    /// <see cref="ContinueReadingService.NextForAsync(IReadOnlyCollection{int}, CancellationToken)"/>
    /// applies (completed, or another chapter of the series in the same
    /// <see cref="ContinueReadingService.SlotOf"/> completed), rather than resolving "what's next"
    /// over every chapter of every series.
    /// </para>
    /// </summary>
    internal static async Task<Dictionary<int, int>> NewChapterCountsAsync(
        MakiDbContext db, int[]? ids, CancellationToken ct)
    {
        var (batches, rows) = await LatestBatchesAsync(db, ids, ct);
        if (batches.Count == 0)
        {
            return [];
        }

        var inBatch = batches.Values.SelectMany(b => b).ToHashSet();
        var batchChapters = rows
            .Where(r => r.ChapterId != null && r.ChapterSeriesId == r.SeriesId && inBatch.Contains(r.FileId))
            .Select(r => (Id: r.ChapterId!.Value, r.SeriesId, r.FileId, r.Number, r.Volume))
            .ToList();
        if (batchChapters.Count == 0)
        {
            return [];
        }

        // The caller's completed rows that can decide a batch chapter: the chapter itself, or another
        // of the series with its number (another language's copy). The slot is checked below. This
        // lookup only narrows the progress read, which keeps every filter, so it may skip them: the
        // visibility filter on the joined chapters stops SQLite from using the (SeriesId, Number) index.
        // Id lists go as one JSON array parameter, since a batch can be the whole library after an import.
        var batchChapterIds = batchChapters.Select(c => c.Id).ToArray();
        var deciders = batchChapterIds.Concat(await (
                from b in db.Chapters.IgnoreQueryFilters()
                where EF.Parameter(batchChapterIds).Contains(b.Id) && b.Number != null
                join c in db.Chapters.IgnoreQueryFilters() on b.SeriesId equals c.SeriesId
                where c.Number != null && c.Number == b.Number && c.Id != b.Id
                select c.Id)
            .ToListAsync(ct)).Distinct().ToArray();
        var reads = (await db.ChapterProgress.AsNoTracking()
                .Where(p => p.Completed && EF.Parameter(deciders).Contains(p.ChapterId))
                .Join(db.Chapters, p => p.ChapterId, c => c.Id,
                    (p, c) => new { ProgressSeriesId = p.SeriesId, c.Id, c.SeriesId, c.Number, c.Volume })
                .ToListAsync(ct))
            .Where(r => batches.ContainsKey(r.ProgressSeriesId))
            .ToList();
        var completed = reads.Select(r => r.Id).ToHashSet();
        var readSlots = reads
            .Select(r => (r.SeriesId, Slot: ContinueReadingService.SlotOf(r.Number, r.Volume)))
            .Where(r => r.Slot is not null)
            .Select(r => (r.SeriesId, r.Slot!.Value))
            .ToHashSet();

        var chapterIdsByFile = batchChapters.ToLookup(c => c.FileId, c => c.Id);
        var unreadBySeries = batchChapters
            .Where(c => !completed.Contains(c.Id) &&
                        !(ContinueReadingService.SlotOf(c.Number, c.Volume) is { } slot && readSlots.Contains((c.SeriesId, slot))))
            .GroupBy(c => c.SeriesId)
            .ToDictionary(g => g.Key, g => (IReadOnlySet<int>)g.Select(c => c.Id).ToHashSet());

        var counts = new Dictionary<int, int>();
        foreach (var (seriesId, unread) in unreadBySeries)
        {
            var count = NewChapterBatch.UnreadFiles(batches[seriesId], id => chapterIdsByFile[id], unread).Count;
            if (count > 0)
            {
                counts[seriesId] = count;
            }
        }

        return counts;
    }

    private const int BatchWindowDays = 7;

    /// <summary>
    /// Each series' <see cref="NewChapterBatch.Latest"/>, reading only its last
    /// <see cref="BatchWindowDays"/> of files first. A batch that starts within one gap of that
    /// window's edge might run on past it, so only those series are read whole. Each file comes with
    /// the chapters it backs, one row per chapter, or one row with no chapter.
    /// </summary>
    private static async Task<(Dictionary<int, List<int>> Batches, List<FileChapterRow> Rows)> LatestBatchesAsync(
        MakiDbContext db, int[]? ids, CancellationToken ct)
    {
        var files = ForSeries(db.ChapterFiles.AsNoTracking(), ids, f => f.SeriesId).Where(ChapterFile.IsNewContent);
        var cutoffs = files
            .GroupBy(f => f.SeriesId)
            .Select(g => new { SeriesId = g.Key, From = g.Max(f => f.DateAdded).AddDays(-BatchWindowDays) });
        var rows = await WithChapters(db, files
                .Join(cutoffs, f => f.SeriesId, n => n.SeriesId, (f, n) => new { File = f, n.From })
                .Where(x => x.File.DateAdded >= x.From)
                .Select(x => x.File))
            .ToListAsync(ct);

        var batches = new Dictionary<int, List<int>>();
        var unbounded = new HashSet<int>();
        foreach (var group in rows.GroupBy(r => r.SeriesId))
        {
            var batch = Latest(group);
            var oldest = group.First(f => f.FileId == batch[^1]).DateAdded;
            var newest = group.Max(f => f.DateAdded);
            // A second of slack for SQLite rounding the cutoff to milliseconds.
            if (newest > DateTime.MinValue.AddDays(BatchWindowDays + 1) &&
                oldest >= newest.AddDays(-BatchWindowDays).AddSeconds(1) + NewChapterBatch.BatchGap)
            {
                batches[group.Key] = batch;
            }
            else
            {
                unbounded.Add(group.Key);
            }
        }

        if (unbounded.Count > 0)
        {
            var seriesIds = unbounded.ToArray();
            var whole = await WithChapters(db, files.Where(f => EF.Parameter(seriesIds).Contains(f.SeriesId))).ToListAsync(ct);
            foreach (var group in whole.GroupBy(r => r.SeriesId))
            {
                batches[group.Key] = Latest(group);
            }

            rows = [.. rows.Where(r => !unbounded.Contains(r.SeriesId)), .. whole];
        }

        return (batches, rows);

        static List<int> Latest(IEnumerable<FileChapterRow> rows) =>
            NewChapterBatch.Latest(rows.DistinctBy(r => r.FileId).Select(r => new NewChapterBatch.File(r.FileId, r.DateAdded)));
    }

    private sealed record FileChapterRow(
        int FileId, int SeriesId, DateTime DateAdded, int? ChapterId, int? ChapterSeriesId, decimal? Number, int? Volume);

    private static IQueryable<FileChapterRow> WithChapters(MakiDbContext db, IQueryable<ChapterFile> files) =>
        from f in files
        join c in db.Chapters on (int?)f.Id equals c.ChapterFileId into backed
        from c in backed.DefaultIfEmpty()
        select new FileChapterRow(f.Id, f.SeriesId, f.DateAdded, (int?)c!.Id, (int?)c.SeriesId, c.Number, c.Volume);

    internal sealed record ChapterTallies(
        int SeriesId, int Wanted, int WithFile, int Known, int Removed, int Specials, int SpecialsHad, double? HighestMain);

    /// <summary>
    /// The chapter counts every series surface reports, keyed by series id. The list and detail
    /// endpoints must agree on these, so they share one expression rather than each spelling it out.
    /// <para>
    /// <c>Wanted</c> counts what the user asked for plus anything already on disk: a chapter they
    /// don't want and don't have (a skipped special) is excluded so a fully-downloaded series reads
    /// 39/39 rather than 39/40, while one they don't want but already have still counts on both
    /// sides. Chapters merely waiting to download are wanted, so deferring never shrinks this. A
    /// chapter whose file was removed on purpose counts too, so clearing read files never shrinks it.
    /// </para>
    /// </summary>
    internal static async Task<Dictionary<int, ChapterTallies>> ChapterTalliesAsync(
        IQueryable<Chapter> chapters, CancellationToken ct) =>
        await chapters
            .GroupBy(c => c.SeriesId)
            .Select(g => new ChapterTallies(
                g.Key,
                g.Count(c => c.Wanted || c.ChapterFileId != null || c.FileRemovedAt != null),
                g.Count(c => c.ChapterFileId != null),
                g.Count(),
                g.Count(c => c.ChapterFileId == null && c.FileRemovedAt != null),
                // Chapter.IsSpecial in SQL: Number is stored as REAL, and the cast round trip is
                // what EF translates (CAST(CAST(Number AS INTEGER) AS REAL)).
                g.Count(c => c.Number != null && c.Number != (decimal)(long)c.Number),
                g.Count(c => c.Number != null && c.Number != (decimal)(long)c.Number &&
                             (c.ChapterFileId != null || c.FileRemovedAt != null)),
                g.Where(c => c.Number != null && c.Number == (decimal)(long)c.Number).Max(c => (double?)(long)c.Number!.Value)))
            .ToDictionaryAsync(x => x.SeriesId, ct);

    /// <summary>
    /// Per series, how many of the read chapters <see cref="ReadChapterCountsBySeriesAsync"/> counts
    /// are specials, so <see cref="SeriesProgressScope"/> can leave them out when specials are skipped.
    /// </summary>
    internal static async Task<Dictionary<int, int>> ReadSpecialCountsBySeriesAsync(
        MakiDbContext db, int[]? ids, CancellationToken ct) =>
        await ForSeries(ReadCounts.Read(db), ids, p => p.SeriesId)
            .Where(p => db.Chapters.Any(c => c.Id == p.ChapterId && c.Number != null &&
                                             c.Number != (decimal)(long)c.Number))
            .GroupBy(p => p.SeriesId)
            .Select(g => new { SeriesId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.SeriesId, x => x.Count, ct);

    internal static SeriesProgressScope.Totals ProgressTotals(
        Series series, ChapterTallies? counts, int? read, int readSpecials, bool unmonitorSpecials) =>
        SeriesProgressScope.Compute(
            series, counts?.Known ?? 0, (counts?.WithFile ?? 0) + (counts?.Removed ?? 0),
            counts?.Specials ?? 0, counts?.SpecialsHad ?? 0, counts?.HighestMain,
            read, readSpecials, unmonitorSpecials);

    internal static async Task<bool> UnmonitorSpecialsAsync(IAppSettings appSettings, CancellationToken ct) =>
        await appSettings.GetAsync(SettingKeys.MonitoringUnmonitorSpecials, ct) == "true";

    /// <summary>
    /// Per series, how many of its downloaded chapters are read — a straight count of completed
    /// <see cref="ChapterProgress"/> rows, which is the ground truth for read state from both
    /// sources (the built-in reader, and Kavita through <see cref="ExternalReadSyncService"/>).
    /// Series with no rows at all are absent from the result, so the UI can hide the stat rather
    /// than claiming "0 read".
    /// <para>
    /// Deliberately <b>not</b> derived from <see cref="ReadingState.MaxChapter"/> any more. That
    /// mark is forward-only and covers every chapter numbered below it, so a single stale or
    /// mis-attributed Kavita read left a series permanently reporting chapters read that had never
    /// been opened — and nothing could clear it, because the mark may not be lowered.
    /// </para>
    /// </summary>
    private static async Task<Dictionary<int, int>> ReadChapterCountsBySeriesAsync(
        MakiDbContext db, int[]? ids, CancellationToken ct) =>
        await ForSeries(ReadCounts.Read(db), ids, p => p.SeriesId)
            .GroupBy(p => p.SeriesId)
            .Select(g => new { SeriesId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.SeriesId, x => x.Count, ct);
}
