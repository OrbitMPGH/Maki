using System.Text.Json;
using Maki.Api.Dtos;
using Maki.Core.Configuration;
using Maki.Core.Entities;
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
public class ActivityStatsService(MakiDbContext db, IAppSettings appSettings, TimeProvider clock)
{
    /// <summary>A series counts as dropped when its reading mark stalled this long.</summary>
    private static readonly TimeSpan DroppedAfter = TimeSpan.FromDays(60);

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

    public async Task<List<int>> YearsAsync(int userId, CancellationToken ct)
    {
        return await EventsFor(userId)
            .Select(e => e.Timestamp.Year)
            .Distinct()
            .OrderByDescending(y => y)
            .ToListAsync(ct);
    }

    /// <param name="userId">Whose year this is. Callers must have resolved it through
    /// <see cref="UserViewResolver"/> — this service does no permission checking of its own.</param>
    /// <param name="utcOffsetMinutes">JS getTimezoneOffset() semantics: UTC − local, so
    /// UTC+2 sends −120. Local time = UTC − offset.</param>
    public async Task<ActivityStatsDto> StatsAsync(
        int userId, DateOnly from, DateOnly to, int utcOffsetMinutes, CancellationToken ct)
    {
        // [from, to] are inclusive local dates; convert the window edges to UTC.
        var utcStart = from.ToDateTime(TimeOnly.MinValue).AddMinutes(utcOffsetMinutes);
        var utcEnd = to.AddDays(1).ToDateTime(TimeOnly.MinValue).AddMinutes(utcOffsetMinutes);

        var events = await EventsFor(userId)
            .Where(e => e.Timestamp >= utcStart && e.Timestamp < utcEnd)
            .ToListAsync(ct);

        DateTime Local(DateTime utc) => utc.AddMinutes(-utcOffsetMinutes);

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
                try
                {
                    var snap = JsonSerializer.Deserialize<RemovedSeriesSnapshot>(payloadJson);
                    (genres, tags) = (snap?.Genres, snap?.Tags);
                }
                catch (JsonException)
                {
                    // best-effort — a malformed snapshot just doesn't contribute
                }
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
            .Select(e => new ActivitySeriesEventDto(
                e.SeriesId, e.SeriesTitle, Local(e.Timestamp), Cover(e.SeriesId)))
            .ToList();

        // ---- dropped (computed from ReadingState, not an event — self-heals on resume) ----
        var staleBefore = clock.GetUtcNow().UtcDateTime - DroppedAfter;
        var droppedRows = await db.ReadingStates.AsNoTracking().IgnoreQueryFilters()
            .Where(r => r.UserId == userId &&
                        !r.Finished && r.MaxChapter > 0 &&
                        r.LastProgressAt < staleBefore &&
                        r.LastProgressAt >= utcStart && r.LastProgressAt < utcEnd)
            .ToListAsync(ct);

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
                daysActive),
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

    private sealed record RemovedSeriesSnapshot(
        [property: System.Text.Json.Serialization.JsonPropertyName("genres")] List<string>? Genres,
        [property: System.Text.Json.Serialization.JsonPropertyName("tags")] List<string>? Tags);
}
