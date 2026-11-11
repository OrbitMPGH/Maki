using System.Globalization;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

public record SeriesReadingTimeBucket(string Bucket, int Seconds);

public record SeriesReadingStats(
    int Seconds,
    int TimedChapters,
    int? ChapterMedianSeconds,
    int? ElsewhereMedianSeconds,
    int Sittings,
    DateTime FirstReadAt,
    DateTime LastReadAt,
    string BucketUnit,
    IReadOnlyList<SeriesReadingTimeBucket> Timeline);

/// <summary>
/// One user's reading time in one series, for the series page's "Your reading" panel. Only the
/// built-in reader records time, so a series read through Kavita, OPDS or any other app has none
/// and gets null: the panel is absent rather than a box of zeros.
/// <para>
/// The total and the per-chapter pace come from <see cref="ChapterProgress.ReadSeconds"/>, which is
/// current to the last report. Sittings and the timeline come from the
/// <see cref="StatsEventType.ReadingTime"/> log, which is dated but lags by up to one unflushed chunk
/// and is empty for a fully incognito series.
/// </para>
/// </summary>
public class SeriesReadingStatsService(MakiDbContext db, IUserSettingsStore userSettings)
{
    private const int MinimumSamples = 3;
    private const int MinimumWeeks = 8;
    private const int MaximumWeeks = 26;
    private const int MaximumMonths = 24;

    public async Task<SeriesReadingStats?> ForAsync(
        int userId, int seriesId, string? seriesType, DateTime nowUtc, CancellationToken ct = default)
    {
        var rows = await db.ChapterProgress.AsNoTracking()
            .Where(p => p.UserId == userId && p.SeriesId == seriesId && p.ReadSeconds > 0)
            .Select(p => new { p.ReadSeconds, p.Completed, p.Watched, p.StartedAt, p.UpdatedAt })
            .ToListAsync(ct);
        if (rows.Count == 0)
        {
            return null;
        }

        var paced = rows.Where(r => r.Completed && !r.Watched).Select(r => r.ReadSeconds).ToList();

        var scrolling = seriesType is SeriesTypes.Manhua or SeriesTypes.Manhwa;
        var elsewhere = await db.ChapterProgress.AsNoTracking()
            .Where(p => p.UserId == userId && p.SeriesId != seriesId &&
                        p.Completed && !p.Watched && p.ReadSeconds > 0)
            .Join(db.Series, p => p.SeriesId, s => s.Id, (p, s) => new { p.ReadSeconds, p.UpdatedAt, s.Type })
            .Where(x => scrolling
                ? x.Type == SeriesTypes.Manhua || x.Type == SeriesTypes.Manhwa
                : x.Type != SeriesTypes.Manhua && x.Type != SeriesTypes.Manhwa)
            .OrderByDescending(x => x.UpdatedAt)
            .Select(x => x.ReadSeconds)
            .Take(100)
            .ToListAsync(ct);

        var events = await db.StatsEvents.AsNoTracking()
            .Where(e => e.UserId == userId && e.SeriesId == seriesId &&
                        e.Type == StatsEventType.ReadingTime && e.Value > 0)
            .OrderBy(e => e.Timestamp)
            .Select(e => new { e.Timestamp, e.Value })
            .ToListAsync(ct);

        var sittings = 0;
        DateTime? sittingEnd = null;
        foreach (var e in events)
        {
            var start = e.Timestamp.AddSeconds(-e.Value);
            if (sittingEnd is null || start > sittingEnd.Value + ReadingSessionService.Gap)
            {
                sittings++;
            }

            sittingEnd = sittingEnd is { } end && end > e.Timestamp ? end : e.Timestamp;
        }

        var firstRead = events.Count > 0
            ? events[0].Timestamp.AddSeconds(-events[0].Value)
            : rows.Min(r => r.StartedAt);
        var lastRead = rows.Max(r => r.UpdatedAt);
        if (events.Count > 0 && events[^1].Timestamp > lastRead)
        {
            lastRead = events[^1].Timestamp;
        }

        var zone = await UserTimeZone.ResolveAsync(userSettings, userId, ct);
        var (unit, timeline) = events.Count == 0
            ? ("week", Array.Empty<SeriesReadingTimeBucket>())
            : Timeline(events.Select(e => (e.Timestamp, e.Value)).ToList(), zone, nowUtc);

        return new SeriesReadingStats(
            rows.Sum(r => r.ReadSeconds),
            rows.Count,
            paced.Count >= MinimumSamples ? Median(paced) : null,
            elsewhere.Count >= MinimumSamples ? Median(elsewhere) : null,
            sittings,
            DateTime.SpecifyKind(firstRead, DateTimeKind.Utc),
            DateTime.SpecifyKind(lastRead, DateTimeKind.Utc),
            unit,
            timeline);
    }

    /// <summary>
    /// Weeks (Monday first) from the first read to this week, at least eight so a new series does not
    /// draw one bar across the whole panel. Past 26 weeks it switches to months, the last 24 of them.
    /// </summary>
    internal static (string Unit, IReadOnlyList<SeriesReadingTimeBucket> Buckets) Timeline(
        IReadOnlyList<(DateTime At, int Seconds)> events, TimeZoneInfo zone, DateTime nowUtc)
    {
        DateOnly Local(DateTime utc) =>
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone));
        static DateOnly WeekOf(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
        static DateOnly MonthOf(DateOnly day) => new(day.Year, day.Month, 1);

        var today = Local(nowUtc);
        var first = Local(events[0].At);
        var thisWeek = WeekOf(today);
        var weeks = (thisWeek.DayNumber - WeekOf(first).DayNumber) / 7 + 1;

        if (weeks <= MaximumWeeks)
        {
            var count = Math.Max(MinimumWeeks, weeks);
            var start = thisWeek.AddDays(-7 * (count - 1));
            var sums = new int[count];
            foreach (var (at, seconds) in events)
            {
                var index = (WeekOf(Local(at)).DayNumber - start.DayNumber) / 7;
                if (index >= 0 && index < count) sums[index] += seconds;
            }

            return ("week", sums
                .Select((s, i) => new SeriesReadingTimeBucket(
                    start.AddDays(7 * i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), s))
                .ToList());
        }

        var thisMonth = MonthOf(today);
        var months = (thisMonth.Year - first.Year) * 12 + thisMonth.Month - first.Month + 1;
        var monthCount = Math.Min(MaximumMonths, months);
        var firstMonth = thisMonth.AddMonths(-(monthCount - 1));
        var monthSums = new int[monthCount];
        foreach (var (at, seconds) in events)
        {
            var day = Local(at);
            var index = (day.Year - firstMonth.Year) * 12 + day.Month - firstMonth.Month;
            if (index >= 0 && index < monthCount) monthSums[index] += seconds;
        }

        return ("month", monthSums
            .Select((s, i) => new SeriesReadingTimeBucket(
                firstMonth.AddMonths(i).ToString("yyyy-MM", CultureInfo.InvariantCulture), s))
            .ToList());
    }

    private static int Median(List<int> values)
    {
        values.Sort();
        var middle = values.Count / 2;
        return values.Count % 2 == 0
            ? (int)Math.Round((values[middle - 1] + values[middle]) / 2d)
            : values[middle];
    }
}
