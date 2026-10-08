using System.Text.Json;
using Maki.Api.Dtos;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Aggregates the append-only StatsEvents log into one window of reading activity. Feeds the
/// Stats page's Overview tab, and the Rewind slideshow off the same payload.
/// <para>
/// On-demand and in-memory by design: a heavy year is low tens of thousands of tiny rows (one
/// indexed range query), SQLite's date functions don't translate timezone shifts, and the
/// genre/tag step needs an in-memory join against Series' JSON list columns anyway.
/// </para>
/// </summary>
public class ActivityStatsService(
    MakiDbContext db, IAppSettings appSettings, IUserSettingsStore userSettings, TimeProvider clock,
    ICurrentUser currentUser)
{
    /// <summary>A series counts as dropped when its reading mark stalled this long.</summary>
    internal static readonly TimeSpan DroppedAfter = TimeSpan.FromDays(60);

    private const int TimelineDayBucketMaxDays = 62;

    /// <summary>
    /// Events for one reader: their own, plus the library-wide ones that belong to nobody.
    /// <para>
    /// Filters are ignored and the predicate written out rather than leaning on the global
    /// <c>StatsEvent</c> filter, because an admin may be looking at somebody else's year and the
    /// ambient scope is still their own. Mirrors <see cref="UserMetricsService"/>.
    /// </para>
    /// </summary>
    private IQueryable<StatsEvent> EventsFor(int userId) =>
        db.StatsEvents.AsNoTracking().IgnoreQueryFilters()
            .Where(e => e.UserId == null || e.UserId == userId);

    /// <summary>
    /// Same resolution as <see cref="StatsInsightsService.GetAsync"/>: the reader's stored zone
    /// when they have one (correct across DST), else a fixed offset built from the browser's
    /// current one.
    /// </summary>
    private async Task<TimeZoneInfo> ResolveZoneAsync(int userId, int utcOffsetMinutes, CancellationToken ct) =>
        await UserTimeZone.TryResolveAsync(userSettings, userId, ct) ?? StatsInsightsService.FixedOffset(utcOffsetMinutes);

    /// <summary>
    /// No real-world zone shifts a timestamp across a year boundary by more than this; used to keep
    /// the boundary checks in <see cref="YearsAsync"/> to a handful of rows either side of Jan 1
    /// rather than a full-table scan.
    /// </summary>
    private static readonly TimeSpan MaxZoneShift = TimeSpan.FromHours(14);

    /// <param name="utcOffsetMinutes">JS getTimezoneOffset() semantics, used only when the reader
    /// has no stored time zone.</param>
    /// <remarks>
    /// Distinct UTC years come straight out of SQL (translated to <c>strftime('%Y', ...)</c>), which
    /// is exact for every event except one within <see cref="MaxZoneShift"/> of a year boundary, the
    /// only place a zone conversion can move a timestamp into the neighboring year. Those few rows,
    /// and only those, are pulled into memory and converted for real.
    /// </remarks>
    public async Task<List<int>> YearsAsync(int userId, int utcOffsetMinutes, CancellationToken ct)
    {
        var zone = await ResolveZoneAsync(userId, utcOffsetMinutes, ct);
        if (!currentUser.AllRootFolders)
        {
            var all = await EventsFor(userId).ToListAsync(ct);
            var ids = all.Where(e => e.SeriesId != null).Select(e => e.SeriesId!.Value).Distinct().ToList();
            var visibleIds = (await db.Series.AsNoTracking().Where(s => ids.Contains(s.Id)).Select(s => s.Id)
                .ToListAsync(ct)).ToHashSet();
            var visible = await HideUnseenFoldersAsync(all, visibleIds, ct);
            return visible
                .Select(e => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(e.Timestamp, DateTimeKind.Utc), zone).Year)
                .Distinct()
                .OrderByDescending(y => y)
                .ToList();
        }

        var utcYears = await EventsFor(userId).Select(e => e.Timestamp.Year).Distinct().ToListAsync(ct);

        var years = new HashSet<int>();
        var checkedBoundaries = new HashSet<DateTime>();

        int LocalYear(DateTime utc) =>
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone).Year;

        async Task CheckBoundaryAsync(DateTime boundaryUtc)
        {
            if (!checkedBoundaries.Add(boundaryUtc))
            {
                return;
            }

            var windowStart = boundaryUtc - MaxZoneShift;
            var windowEnd = boundaryUtc + MaxZoneShift;
            var nearBoundary = EventsFor(userId).Where(e => e.Timestamp >= windowStart && e.Timestamp < windowEnd);
            if (!await nearBoundary.AnyAsync(ct))
            {
                return;
            }

            var timestamps = await nearBoundary.Select(e => e.Timestamp).ToListAsync(ct);
            foreach (var t in timestamps)
            {
                years.Add(LocalYear(t));
            }
        }

        foreach (var y in utcYears)
        {
            var yearStart = new DateTime(y, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var yearEnd = new DateTime(y + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            // Any event far enough from both boundaries can't have shifted out of this UTC year, so
            // its presence alone guarantees the year survives locally.
            var hasDeepEvent = await EventsFor(userId).AnyAsync(
                e => e.Timestamp >= yearStart + MaxZoneShift && e.Timestamp < yearEnd - MaxZoneShift, ct);
            if (hasDeepEvent)
            {
                years.Add(y);
            }

            await CheckBoundaryAsync(yearStart);
            await CheckBoundaryAsync(yearEnd);
        }

        return years.OrderByDescending(y => y).ToList();
    }

    /// <param name="userId">Whose year this is. Callers must have resolved it through
    /// <see cref="UserViewResolver"/> — this service does no permission checking of its own.</param>
    /// <param name="utcOffsetMinutes">JS getTimezoneOffset() semantics, used only when the reader
    /// has no stored time zone.</param>
    public async Task<ActivityStatsDto> StatsAsync(
        int userId, DateOnly from, DateOnly to, int utcOffsetMinutes, CancellationToken ct)
    {
        var zone = await ResolveZoneAsync(userId, utcOffsetMinutes, ct);

        // [from, to] are inclusive local dates; convert the window edges to UTC.
        var utcStart = StatsInsightsService.ToUtc(from.ToDateTime(TimeOnly.MinValue), zone);
        var utcEnd = StatsInsightsService.ToUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue), zone);

        var events = await EventsFor(userId)
            .Where(e => e.Timestamp >= utcStart && e.Timestamp < utcEnd)
            .ToListAsync(ct);

        DateTime Local(DateTime utc) =>
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);

        // Loaded up front because every list below wants a cover for it. One query either way —
        // the projection just carries three columns instead of two.
        var seriesIds = events.Where(e => e.SeriesId != null).Select(e => e.SeriesId!.Value).Distinct().ToList();
        // Query filter left ON deliberately: this is the one join that reaches live library rows,
        // and a series in a root folder the caller cannot see should not hand back its cover or
        // its genres. Events keep their denormalized title either way.
        var seriesMeta = await db.Series.AsNoTracking()
            .Where(s => seriesIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Genres, s.Tags, s.CoverPath, s.LastMetadataRefresh })
            .ToDictionaryAsync(s => s.Id, ct);

        events = await HideUnseenFoldersAsync(events, seriesMeta.Keys.ToHashSet(), ct);

        // Null for a removed series, which keeps its denormalized title but not its cover file.
        string? Cover(int? seriesId) =>
            seriesId is int sid && seriesMeta.TryGetValue(sid, out var meta)
                ? SeriesDto.CoverUrlFor(sid, meta.CoverPath, meta.LastMetadataRefresh)
                : null;

        // How one series' events find each other. SeriesKey first: it is the only identity a hard
        // delete cannot sever, so a series removed and added back aggregates as one entry instead
        // of an orphaned half and a live half. The rest are fallbacks for rows written before the
        // key existed and never repaired.
        static string GroupKey(StatsEvent e) =>
            e.SeriesKey
            ?? (e.SeriesId is int sid ? $"s{sid}"
                : e.KavitaSeriesId is int kid ? $"k{kid}"
                : e.SeriesTitle);

        // Pick the identity to show for a group. The newest event that still resolves to a live
        // series wins, so a group spanning a delete and a re-add gets the current row's cover and
        // link rather than the orphaned half's nothing. Falls back to the newest event's title for
        // a series that is still gone.
        T Identify<T>(IEnumerable<StatsEvent> group, Func<int?, string, string?, T> build)
        {
            var ordered = group.OrderBy(e => e.Timestamp).ToList();
            var live = ordered.LastOrDefault(e => e.SeriesId is int id && seriesMeta.ContainsKey(id))
                       ?? ordered.LastOrDefault(e => e.SeriesId != null);
            var named = live ?? ordered[^1];
            return build(named.SeriesId, named.SeriesTitle, Cover(named.SeriesId));
        }

        // ---- totals ----
        int Sum(StatsEventType t) => events.Where(e => e.Type == t).Sum(e => e.Value);
        int Count(StatsEventType t) => events.Count(e => e.Type == t);

        var daysActive = events
            .Where(e => e.Type is StatsEventType.ChaptersRead or StatsEventType.VolumesRead)
            .Select(e => Local(e.Timestamp).Date)
            .Distinct()
            .Count();

        // ---- timeline ----
        var useDayBuckets = to.DayNumber - from.DayNumber + 1 <= TimelineDayBucketMaxDays;
        string Bucket(DateTime utc)
        {
            var local = Local(utc);
            return useDayBuckets ? local.ToString("yyyy-MM-dd") : local.ToString("yyyy-MM");
        }

        var timeline = events
            .Where(e => e.Type is StatsEventType.ChaptersRead or StatsEventType.ChapterDownloaded
                or StatsEventType.SeriesAdded or StatsEventType.ReadingTime)
            .GroupBy(e => Bucket(e.Timestamp))
            .OrderBy(g => g.Key)
            .Select(g => new ActivityTimelinePointDto(
                g.Key,
                g.Where(e => e.Type == StatsEventType.ChaptersRead).Sum(e => e.Value),
                g.Where(e => e.Type == StatsEventType.ChapterDownloaded).Sum(e => e.Value),
                g.Where(e => e.Type == StatsEventType.SeriesAdded).Sum(e => e.Value),
                g.Where(e => e.Type == StatsEventType.ReadingTime).Sum(e => e.Value)))
            .ToList();

        // ---- most/least read ----
        var readEvents = events
            .Where(e => e.Type is StatsEventType.ChaptersRead or StatsEventType.VolumesRead)
            .ToList();
        var perSeries = readEvents
            .GroupBy(GroupKey)
            .Select(g => Identify(g, (id, title, cover) =>
                new ActivitySeriesStatDto(id, title, g.Sum(e => e.Value), cover)))
            .ToList();
        var topRead = perSeries.OrderByDescending(s => s.Count).ThenBy(s => s.Title).Take(10).ToList();

        // ---- where the time went ----
        // Deliberately not folded into perSeries: these events carry seconds rather than a count
        // of chapters, and summing the two together would report a series as read 4,000 times.
        var topByTime = events
            .Where(e => e.Type == StatsEventType.ReadingTime)
            .GroupBy(GroupKey)
            .Select(g => Identify(g, (id, title, cover) =>
                new ActivitySeriesTimeDto(id, title, g.Sum(e => e.Value), cover)))
            .OrderByDescending(s => s.Seconds).ThenBy(s => s.Title)
            .Take(10)
            .ToList();

        var topKeys = topRead.Select(s => (s.SeriesId, s.Title)).ToHashSet();
        var leastRead = perSeries
            .Where(s => s.Count >= 1 && !topKeys.Contains((s.SeriesId, s.Title)))
            .OrderBy(s => s.Count).ThenBy(s => s.Title)
            .Take(5)
            .ToList();

        // ---- favorite genres/tags ----
        // Weight = chapters/volumes read per series; when nothing was read in the window
        // (no Kavita), fall back to series added. Removed series contribute via their
        // snapshot payload.
        var genreWeights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var tagWeights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void AddWeights(int? seriesId, string? payloadJson, int weight)
        {
            List<string>? genres = null, tags = null;
            if (seriesId is int sid && seriesMeta.TryGetValue(sid, out var meta))
            {
                (genres, tags) = (meta.Genres, meta.Tags);
            }
            else if (payloadJson is not null)
            {
                var snap = ParseSnapshot(payloadJson);
                (genres, tags) = (snap?.Genres, snap?.Tags);
            }

            foreach (var g in genres ?? [])
            {
                genreWeights[g] = genreWeights.GetValueOrDefault(g) + weight;
            }

            foreach (var t in tags ?? [])
            {
                tagWeights[t] = tagWeights.GetValueOrDefault(t) + weight;
            }
        }

        var weightSource = readEvents.Count > 0
            ? readEvents
            : events.Where(e => e.Type is StatsEventType.SeriesAdded or StatsEventType.SeriesRemoved).ToList();
        foreach (var e in weightSource)
        {
            AddWeights(e.SeriesId, e.PayloadJson, e.Value);
        }

        static List<ActivityWeightedNameDto> Top(Dictionary<string, int> weights) => weights
            .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
            .Take(10)
            .Select(kv => new ActivityWeightedNameDto(kv.Key, kv.Value))
            .ToList();

        // ---- event lists ----
        List<ActivitySeriesEventDto> EventList(StatsEventType type) => events
            .Where(e => e.Type == type)
            .OrderByDescending(e => e.Timestamp)
            .Select(e =>
            {
                var snapshot = ParseSnapshot(e.PayloadJson);
                var providerId = snapshot?.ProviderId
                    ?? (e.SeriesKey?.StartsWith("mb:", StringComparison.Ordinal) == true
                        ? e.SeriesKey[3..]
                        : null);
                return new ActivitySeriesEventDto(
                    e.SeriesId, e.SeriesTitle, Local(e.Timestamp),
                    Cover(e.SeriesId) ?? snapshot?.CoverUrl, providerId);
            })
            .ToList();

        // ---- dropped (computed from ReadingState, not an event — self-heals on resume) ----
        var staleBefore = clock.GetUtcNow().UtcDateTime - DroppedAfter;
        var droppedRows = await db.ReadingStates.AsNoTracking().IgnoreQueryFilters()
            .Where(r => r.UserId == userId &&
                        !r.Finished && r.MaxChapter > 0 &&
                        r.LastProgressAt < staleBefore &&
                        r.LastProgressAt >= utcStart && r.LastProgressAt < utcEnd)
            .ToListAsync(ct);

        var droppedSeriesIds = droppedRows.Where(r => r.SeriesId != null).Select(r => r.SeriesId!.Value).Distinct().ToList();
        if (droppedSeriesIds.Count > 0)
        {
            var shown = (await db.Series.AsNoTracking()
                    .Where(s => droppedSeriesIds.Contains(s.Id) && s.Incognito != IncognitoMode.Full)
                    .Select(s => s.Id)
                    .ToListAsync(ct))
                .ToHashSet();
            droppedRows = droppedRows.Where(r => r.SeriesId is not int sid || shown.Contains(sid)).ToList();
        }

        // These come off ReadingState, not the event log, so their series are not necessarily in
        // seriesMeta — a series can stall in a window where it produced no events at all.
        var droppedIds = droppedRows
            .Where(r => r.SeriesId != null && !seriesMeta.ContainsKey(r.SeriesId.Value))
            .Select(r => r.SeriesId!.Value)
            .Distinct()
            .ToList();
        var droppedCovers = droppedIds.Count == 0
            ? []
            : await db.Series.AsNoTracking()
                .Where(s => droppedIds.Contains(s.Id))
                .Select(s => new { s.Id, s.CoverPath, s.LastMetadataRefresh })
                .ToDictionaryAsync(
                    s => s.Id,
                    s => SeriesDto.CoverUrlFor(s.Id, s.CoverPath, s.LastMetadataRefresh),
                    ct);

        var dropped = droppedRows
            .OrderBy(r => r.LastProgressAt)
            .Select(r => new ActivityDroppedSeriesDto(
                r.SeriesId, r.Title, Local(r.LastProgressAt), r.MaxChapter,
                Cover(r.SeriesId) ??
                (r.SeriesId is int did ? droppedCovers.GetValueOrDefault(did) : null)))
            .ToList();

        // Reading is tracked from Kavita OR from the built-in reader. Gating this on Kavita
        // alone would hide the reads section from a reader-only user who is generating
        // ChaptersRead events right now.
        var readTrackingAvailable =
            (!string.IsNullOrWhiteSpace(await appSettings.GetAsync(SettingKeys.KavitaUrl, ct)) &&
             !string.IsNullOrWhiteSpace(await appSettings.GetAsync(SettingKeys.KavitaApiKey, ct))) ||
            await db.ReadingStates.AsNoTracking().IgnoreQueryFilters()
                .AnyAsync(r => r.UserId == userId, ct);

        var (pagesRead, seriesStarted) = await PagesAndStartsAsync(userId, utcStart, utcEnd, ct);

        return new ActivityStatsDto(
            from, to, readTrackingAvailable,
            new ActivityTotalsDto(
                Sum(StatsEventType.ChaptersRead),
                Sum(StatsEventType.VolumesRead),
                Sum(StatsEventType.ChapterDownloaded),
                Count(StatsEventType.SeriesAdded),
                Count(StatsEventType.SeriesRemoved),
                Count(StatsEventType.SeriesFinished),
                dropped.Count,
                Sum(StatsEventType.ReadingTime),
                daysActive,
                pagesRead,
                seriesStarted),
            timeline,
            topRead,
            leastRead,
            Top(genreWeights),
            Top(tagWeights),
            EventList(StatsEventType.SeriesFinished),
            EventList(StatsEventType.SeriesAdded),
            EventList(StatsEventType.SeriesRemoved),
            dropped,
            topByTime);
    }

    /// <summary>
    /// Pages and series starts come off <c>ChapterProgress</c> rather than the event log, which has
    /// no page numbers. Progress rows are written even for fully incognito series (only their events
    /// are dropped), so those are excluded here by hand, along with anything outside the caller's
    /// root folders. Imports (<c>PageCount == 0</c>) and watched marks are not reading.
    /// </summary>
    private async Task<(int PagesRead, int SeriesStarted)> PagesAndStartsAsync(
        int userId, DateTime utcStart, DateTime utcEnd, CancellationToken ct)
    {
        var visible = (await db.Series.AsNoTracking()
                .Where(s => s.Incognito != IncognitoMode.Full)
                .Select(s => s.Id)
                .ToListAsync(ct))
            .ToHashSet();

        var own = db.ChapterProgress.AsNoTracking().IgnoreQueryFilters()
            .Where(p => p.UserId == userId && !p.Watched && p.PageCount > 0);

        // Windowed on UpdatedAt, so re-reading an old chapter moves its pages into the current window.
        var pageRows = await own
            .Where(p => p.UnreadAt == null && p.ReadSeconds > 0
                && p.UpdatedAt >= utcStart && p.UpdatedAt < utcEnd)
            .Select(p => new { p.SeriesId, p.Completed, p.PageCount, p.PageIndex })
            .ToListAsync(ct);
        var pages = pageRows
            .Where(p => visible.Contains(p.SeriesId))
            .Sum(p => p.Completed ? p.PageCount : Math.Min(p.PageIndex + 1, p.PageCount));

        var firstReads = await own
            .Where(p => p.ReadSeconds > 0)
            .GroupBy(p => p.SeriesId)
            .Select(g => new { SeriesId = g.Key, First = g.Min(p => p.StartedAt) })
            .ToListAsync(ct);
        var started = firstReads.Count(f =>
            f.First >= utcStart && f.First < utcEnd && visible.Contains(f.SeriesId));

        return (pages, started);
    }

    private static RemovedSeriesSnapshot? ParseSnapshot(string? payloadJson)
    {
        if (payloadJson is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RemovedSeriesSnapshot>(payloadJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Library events carry no user, so the query would otherwise hand every one of them to a caller
    /// restricted to some root folders. A live series is kept only when the caller can see it; a
    /// removed one has lost its row, so its root folder comes off the removal snapshot, found by the
    /// durable series key. A legacy snapshot with no folder recorded stays visible, as in
    /// <see cref="StatsInsightsService"/>.
    /// </summary>
    private async Task<List<StatsEvent>> HideUnseenFoldersAsync(
        List<StatsEvent> events, HashSet<int> visibleSeriesIds, CancellationToken ct)
    {
        if (currentUser.AllRootFolders)
        {
            return events;
        }

        var orphanKeys = events
            .Where(e => e.UserId == null && e.SeriesId == null && e.SeriesKey != null)
            .Select(e => e.SeriesKey!)
            .Distinct()
            .ToList();
        var folderByKey = new Dictionary<string, int>();
        if (orphanKeys.Count > 0)
        {
            var removals = await db.StatsEvents.AsNoTracking().IgnoreQueryFilters()
                .Where(e => e.Type == StatsEventType.SeriesRemoved && e.SeriesKey != null &&
                            orphanKeys.Contains(e.SeriesKey) && e.PayloadJson != null)
                .OrderBy(e => e.Timestamp)
                .Select(e => new { e.SeriesKey, e.PayloadJson })
                .ToListAsync(ct);
            foreach (var r in removals)
            {
                if (ParseSnapshot(r.PayloadJson)?.RootFolderId is int folder)
                {
                    folderByKey[r.SeriesKey!] = folder;
                }
            }
        }

        return events.Where(e =>
        {
            if (e.UserId != null)
            {
                return true;
            }

            if (e.SeriesId is int id)
            {
                return visibleSeriesIds.Contains(id);
            }

            var folder = e.Type == StatsEventType.SeriesRemoved ? ParseSnapshot(e.PayloadJson)?.RootFolderId : null;
            if (folder is null && e.SeriesKey != null && folderByKey.TryGetValue(e.SeriesKey, out var byKey))
            {
                folder = byKey;
            }

            return folder is not int f || currentUser.RootFolderIds.Contains(f);
        }).ToList();
    }

    private sealed record RemovedSeriesSnapshot(
        [property: System.Text.Json.Serialization.JsonPropertyName("genres")] List<string>? Genres,
        [property: System.Text.Json.Serialization.JsonPropertyName("tags")] List<string>? Tags,
        [property: System.Text.Json.Serialization.JsonPropertyName("providerId")] string? ProviderId,
        [property: System.Text.Json.Serialization.JsonPropertyName("coverUrl")] string? CoverUrl,
        [property: System.Text.Json.Serialization.JsonPropertyName("rootFolderId")] int? RootFolderId = null);
}
