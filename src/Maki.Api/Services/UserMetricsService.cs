using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Progress;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Maki.Api.Services;

/// <summary>
/// Recomputes one user's <see cref="UserMetrics"/> from the <c>StatsEvents</c> log.
/// <para>
/// Every query here names its user explicitly and bypasses the global filter, rather than leaning on
/// the ambient <c>DataScope</c>. That is what lets one implementation serve all three callers: the
/// signed-in user reading their own page, an admin reading somebody else's, and the evaluator running
/// off a background path with an unrestricted scope. The predicate is not optional — with the filter
/// off, omitting it returns every user's rows instead of none.
/// </para>
/// <para>
/// The shape is the same bounded-scan-then-aggregate-in-memory one <c>RewindService</c> and Home's
/// rails use, for the same reason: the interesting work is day bucketing in a real time zone and
/// per-series gap detection, neither of which SQLite's <c>ORDER BY</c> can express. Because this lands
/// on Home, the result is cached briefly — it is derived, never incremented, so a stale entry is at
/// worst a badge that appears a minute late.
/// </para>
/// </summary>
public class UserMetricsService(
    MakiDbContext db,
    IUserSettingsStore userSettings,
    IMemoryCache cache,
    TimeProvider clock)
{
    /// <summary>How long a computed snapshot is reused. Short: it backs a live progress ring.</summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    /// <summary>
    /// A day the reader missed that does not end their streak, once per rolling seven. Streaks are the
    /// one mechanic here with any capacity to nag, and a counter that resets to zero over a single
    /// busy evening is exactly the loss-aversion pressure Maki's "reflect state, don't demand
    /// babysitting" posture rules out.
    /// </summary>
    private const int GraceDaysPerWeek = 1;

    /// <summary>Days of history the contribution grid carries: 53 weeks, so it always fills.</summary>
    public const int HeatmapDays = 371;

    /// <summary>A gap this long makes picking a series back up worth noticing. Matches Rewind's "dropped" rule.</summary>
    private static readonly TimeSpan AbandonedAfter = TimeSpan.FromDays(90);

    private static string CacheKey(int userId) => $"metrics:{userId}";

    public void Invalidate(int userId) => cache.Remove(CacheKey(userId));

    public async Task<UserMetrics> GetAsync(int userId, CancellationToken ct = default)
    {
        if (cache.TryGetValue<UserMetrics>(CacheKey(userId), out var cached) && cached is not null)
        {
            return cached;
        }

        var metrics = await ComputeAsync(userId, ct);
        cache.Set(CacheKey(userId), metrics, CacheFor);
        return metrics;
    }

    /// <summary>
    /// The user's time zone, or UTC. A bad or unknown id resolves to UTC rather than throwing: the
    /// value arrives from a browser and the set of ids a host recognises is not guaranteed, and a
    /// stats page that 500s because somebody's zone was renamed upstream is a worse failure than a
    /// day boundary in the wrong place.
    /// </summary>
    public Task<TimeZoneInfo> TimeZoneForAsync(int userId, CancellationToken ct = default) =>
        UserTimeZone.ResolveAsync(userSettings, userId, ct);

    private async Task<UserMetrics> ComputeAsync(int userId, CancellationToken ct)
    {
        var tz = await TimeZoneForAsync(userId, ct);

        // One row per event type. Sum and Count are both taken because they mean different things per
        // type: ChaptersRead carries a delta in Value, SeriesFinished is one row per event.
        var totals = await db.StatsEvents.IgnoreQueryFilters()
            .Where(e => e.UserId == userId)
            .GroupBy(e => e.Type)
            .Select(g => new { Type = g.Key, Sum = g.Sum(x => (long)x.Value), Count = (long)g.Count() })
            .ToListAsync(ct);

        long Sum(StatsEventType type) => totals.FirstOrDefault(t => t.Type == type)?.Sum ?? 0;
        long Count(StatsEventType type) => totals.FirstOrDefault(t => t.Type == type)?.Count ?? 0;

        // The dated scan. Only the columns the bucketing needs, ordered so the per-series gap walk
        // below is a single pass.
        var events = await db.StatsEvents.IgnoreQueryFilters()
            .Where(e => e.UserId == userId &&
                        (e.Type == StatsEventType.ChaptersRead ||
                         e.Type == StatsEventType.VolumesRead ||
                         e.Type == StatsEventType.ReadingTime))
            .OrderBy(e => e.Timestamp)
            .Select(e => new ReadEvent(e.Type, e.Timestamp, e.Value, e.SeriesId))
            .ToListAsync(ct);

        var days = BucketDays(events, tz);
        var (current, longest) = Streaks(days.Select(d => d.Date).ToList(), Today(tz));

        var seriesRead = events.Where(e => e.SeriesId != null).Select(e => e.SeriesId!.Value).ToHashSet();
        var (genres, types) = await BreadthAsync(seriesRead, ct);

        return new UserMetrics
        {
            ChaptersRead = Sum(StatsEventType.ChaptersRead),
            VolumesRead = Sum(StatsEventType.VolumesRead),
            ReadingSeconds = Sum(StatsEventType.ReadingTime),
            SeriesFinished = Count(StatsEventType.SeriesFinished),

            DaysRead = days.Count,
            CurrentStreak = current,
            LongestStreak = longest,
            Days = [.. days.OrderByDescending(d => d.Date).Take(HeatmapDays)],

            DistinctGenres = genres,
            TypesRead = types,

            BestDaySeconds = days.Count == 0 ? 0 : days.Max(d => d.Seconds),
            BestWeekendSeconds = BestWeekend(days),

            LongestSeriesFinished = await LongestFinishedAsync(userId, ct),
            SeriesFullyRead = await FullyReadAsync(userId, ct),

            ReadAfterMidnight = events.Any(e => HourIn(e.Timestamp, tz, 1, 4)),
            ReadAtDawn = events.Any(e => HourIn(e.Timestamp, tz, 5, 7)),
            ResumedAbandonedSeries = ResumedAfterGap(events),
            ReadOnNewYearsDay = days.Any(d => d.Date is { Month: 1, Day: 1 }),

            LibrarySeries = await db.Series.IgnoreQueryFilters().LongCountAsync(ct),
            ChaptersDownloaded = await db.StatsEvents.IgnoreQueryFilters()
                .Where(e => e.UserId == null && e.Type == StatsEventType.ChapterDownloaded)
                .SumAsync(e => (long)e.Value, ct),
        };
    }

    private record ReadEvent(StatsEventType Type, DateTime Timestamp, int Value, int? SeriesId);

    /// <summary>
    /// How much of a goal's period has been achieved so far. Measured over the user's own local
    /// calendar, so "today" ends when their day does and a week starts on Monday rather than on
    /// whatever the host's culture happens to say.
    /// </summary>
    public async Task<long> GoalProgressAsync(
        int userId, GoalPeriod period, GoalMetric metric, CancellationToken ct = default)
    {
        var tz = await TimeZoneForAsync(userId, ct);
        var from = PeriodStart(Today(tz), period);

        if (metric == GoalMetric.SeriesFinished)
        {
            // Not derivable from the day buckets: those carry chapters and seconds, and a finish is
            // neither.
            var fromUtc = TimeZoneInfo.ConvertTimeToUtc(from.ToDateTime(TimeOnly.MinValue), tz);
            return await db.StatsEvents.IgnoreQueryFilters()
                .Where(e => e.UserId == userId && e.Type == StatsEventType.SeriesFinished && e.Timestamp >= fromUtc)
                .LongCountAsync(ct);
        }

        var snapshot = await GetAsync(userId, ct);
        var days = snapshot.Days.Where(d => d.Date >= from).ToList();

        return metric == GoalMetric.Chapters
            ? days.Sum(d => (long)d.Chapters)
            : days.Sum(d => (long)d.Seconds) / 60;
    }

    /// <summary>First local date of the period <paramref name="today"/> falls in. Weeks start Monday.</summary>
    internal static DateOnly PeriodStart(DateOnly today, GoalPeriod period) => period switch
    {
        GoalPeriod.Day => today,
        GoalPeriod.Week => today.AddDays(-(((int)today.DayOfWeek + 6) % 7)),
        GoalPeriod.Month => new DateOnly(today.Year, today.Month, 1),
        GoalPeriod.Year => new DateOnly(today.Year, 1, 1),
        _ => today,
    };

    private DateOnly Today(TimeZoneInfo tz) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(clock.GetUtcNow().UtcDateTime, tz));

    private static DateOnly LocalDate(DateTime utc, TimeZoneInfo tz) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz));

    private static bool HourIn(DateTime utc, TimeZoneInfo tz, int fromInclusive, int toExclusive)
    {
        var hour = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz).Hour;
        return hour >= fromInclusive && hour < toExclusive;
    }

    private static List<ReadingDay> BucketDays(List<ReadEvent> events, TimeZoneInfo tz)
    {
        var byDate = new Dictionary<DateOnly, (int Chapters, int Seconds)>();
        foreach (var e in events)
        {
            var date = LocalDate(e.Timestamp, tz);
            byDate.TryGetValue(date, out var bucket);
            if (e.Type == StatsEventType.ReadingTime)
            {
                bucket.Seconds += e.Value;
            }
            else
            {
                bucket.Chapters += e.Value;
            }

            byDate[date] = bucket;
        }

        return [.. byDate.Select(kv => new ReadingDay(kv.Key, kv.Value.Chapters, kv.Value.Seconds))
            .OrderBy(d => d.Date)];
    }

    /// <summary>
    /// Current and longest run of consecutive reading days, forgiving one skipped day per rolling
    /// seven. Today never breaks a streak: the day is not over, so a reader who has not opened Maki
    /// yet this morning is still on their run.
    /// </summary>
    internal static (long Current, long Longest) Streaks(IReadOnlyList<DateOnly> dates, DateOnly today)
    {
        if (dates.Count == 0)
        {
            return (0, 0);
        }

        var ordered = dates.Distinct().OrderBy(d => d).ToList();

        long longest = 0, run = 0;
        var graceUsed = new List<DateOnly>();

        for (var i = 0; i < ordered.Count; i++)
        {
            if (i == 0)
            {
                run = 1;
            }
            else
            {
                var gap = ordered[i].DayNumber - ordered[i - 1].DayNumber;
                if (gap == 1)
                {
                    run++;
                }
                else if (gap == 2 && CanUseGrace(graceUsed, ordered[i]))
                {
                    // One missed day, forgiven. The skipped day counts toward the run: the reader
                    // did keep the habit either side of it, which is what the number is about.
                    graceUsed.Add(ordered[i - 1].AddDays(1));
                    run += 2;
                }
                else
                {
                    run = 1;
                    graceUsed.Clear();
                }
            }

            longest = Math.Max(longest, run);
        }

        // The run is only "current" if it reaches today or yesterday. Anything older ended.
        var last = ordered[^1];
        var since = today.DayNumber - last.DayNumber;
        var current = since <= 1 ? run : 0;

        return (current, longest);
    }

    private static bool CanUseGrace(List<DateOnly> used, DateOnly at) =>
        used.Count(d => at.DayNumber - d.DayNumber < 7) < GraceDaysPerWeek;

    /// <summary>Most reading seconds across one Saturday and the Sunday immediately after it.</summary>
    private static long BestWeekend(List<ReadingDay> days)
    {
        var seconds = days.ToDictionary(d => d.Date, d => (long)d.Seconds);
        long best = 0;
        foreach (var day in days.Where(d => d.Date.DayOfWeek == DayOfWeek.Saturday))
        {
            seconds.TryGetValue(day.Date.AddDays(1), out var sunday);
            best = Math.Max(best, day.Seconds + sunday);
        }

        return best;
    }

    /// <summary>
    /// Whether any series was picked back up after being untouched for 90 days. A single pass over the
    /// events grouped by series, rather than a <c>ReadingState</c> read: that table carries a
    /// last-progress stamp but legally holds duplicate rows per series, so it cannot answer "was there
    /// a gap" without the same walk anyway.
    /// </summary>
    private static bool ResumedAfterGap(List<ReadEvent> events)
    {
        var last = new Dictionary<int, DateTime>();
        foreach (var e in events)
        {
            if (e.SeriesId is not int id)
            {
                continue;
            }

            if (last.TryGetValue(id, out var previous) && e.Timestamp - previous >= AbandonedAfter)
            {
                return true;
            }

            last[id] = e.Timestamp;
        }

        return false;
    }

    /// <summary>Genres and types across every series this user has read a chapter of.</summary>
    private async Task<(long Genres, IReadOnlySet<string> Types)> BreadthAsync(
        HashSet<int> seriesIds, CancellationToken ct)
    {
        if (seriesIds.Count == 0)
        {
            return (0, new HashSet<string>());
        }

        // Genres is a delimited string behind a value converter, so it cannot be grouped in SQL. The
        // input is bounded by how many series the user has actually read, which is what makes pulling
        // the rows acceptable here where an unbounded scan would not be.
        var rows = await db.Series.IgnoreQueryFilters()
            .Where(s => seriesIds.Contains(s.Id))
            .Select(s => new { s.Genres, s.Type })
            .ToListAsync(ct);

        var genres = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            foreach (var genre in row.Genres.Where(g => !string.IsNullOrWhiteSpace(g)))
            {
                genres.Add(genre.Trim());
            }

            if (!string.IsNullOrWhiteSpace(row.Type))
            {
                types.Add(row.Type.Trim().ToLowerInvariant());
            }
        }

        return (genres.Count, types);
    }

    /// <summary>Chapter count of the longest series this user has finished.</summary>
    private async Task<long> LongestFinishedAsync(int userId, CancellationToken ct)
    {
        var finished = await db.StatsEvents.IgnoreQueryFilters()
            .Where(e => e.UserId == userId && e.Type == StatsEventType.SeriesFinished && e.SeriesId != null)
            .Select(e => e.SeriesId!.Value)
            .Distinct()
            .ToListAsync(ct);

        if (finished.Count == 0)
        {
            return 0;
        }

        return await db.Chapters.IgnoreQueryFilters()
            .Where(c => finished.Contains(c.SeriesId))
            .GroupBy(c => c.SeriesId)
            .Select(g => (long)g.Count())
            .OrderByDescending(n => n)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Series where every downloaded chapter is read. Fully-incognito series are excluded explicitly:
    /// this is the one metric read from <c>ChapterProgress</c> rather than from the event log, and
    /// those rows exist for incognito reading, so the gate that comes free everywhere else has to be
    /// written out here.
    /// </summary>
    private async Task<long> FullyReadAsync(int userId, CancellationToken ct)
    {
        var read = await ReadCounts.ReadFor(db, userId)
            .GroupBy(p => p.SeriesId)
            .Select(g => new { SeriesId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        if (read.Count == 0)
        {
            return 0;
        }

        var candidates = read.Select(r => r.SeriesId).ToList();

        var downloaded = await db.Chapters.IgnoreQueryFilters()
            .Where(c => candidates.Contains(c.SeriesId) && c.ChapterFileId != null)
            .GroupBy(c => c.SeriesId)
            .Select(g => new { SeriesId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.SeriesId, x => x.Count, ct);

        var incognito = await db.Series.IgnoreQueryFilters()
            .Where(s => candidates.Contains(s.Id) && s.Incognito == IncognitoMode.Full)
            .Select(s => s.Id)
            .ToListAsync(ct);

        return read.Count(r =>
            !incognito.Contains(r.SeriesId) &&
            downloaded.TryGetValue(r.SeriesId, out var total) &&
            total > 0 &&
            r.Count >= total);
    }
}
