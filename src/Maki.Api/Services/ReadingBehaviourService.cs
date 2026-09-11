using Maki.Api.Dtos;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>A series named as an example of how the reader reads, not of what they read.</summary>
public record BehaviourSeries(int SeriesId, string Title, string? CoverUrl, string Value);

/// <summary>
/// How somebody reads, as opposed to what. Every field is null when there is not enough to say,
/// rather than zero: "no answer" and "the answer is nothing" are different, and a reader with no
/// timed chapters has not read infinitely fast.
/// </summary>
/// <param name="FinishRate">
/// Of the series they started, the share they read to the end of what they hold. Downloaded
/// chapters are the denominator, not the series' real length: nobody can finish chapters that are
/// not on disk, and counting them would make every ongoing series look abandoned.
/// </param>
/// <param name="MedianStopPoint">
/// How far into the ones they did not finish they got, as a share. The number behind "you bail
/// around a fifth of the way in".
/// </param>
/// <param name="MedianSecondsPerChapter">
/// Typical chapter, over the chapters that carry a time at all. Median rather than mean because a
/// single tab left open overnight moves a mean and not a median.
/// </param>
/// <param name="TimedChapters">
/// How many chapters that median rests on. Reported because the native reader is the only thing
/// that records time: an OPDS or Kavita reader can have thousands of reads and no timed ones.
/// </param>
/// <param name="BiggestDayCount">Most chapters finished in one day, in the reader's own time zone.</param>
public record ReadingBehaviour(
    int SeriesStarted,
    int SeriesFinished,
    double? FinishRate,
    double? MedianStopPoint,
    double? MedianSecondsPerChapter,
    int TimedChapters,
    int ChaptersRead,
    int ReadingDays,
    double? MedianChaptersPerReadingDay,
    int? BiggestDayCount,
    DateOnly? BiggestDay,
    IReadOnlyList<BehaviourSeries> Savoured,
    IReadOnlyList<BehaviourSeries> Devoured,
    IReadOnlyList<BehaviourSeries> Abandoned,
    DateTime GeneratedAt);

/// <summary>
/// The reading habits already implied by <c>ChapterProgress</c> and thrown away everywhere else.
///
/// <para>
/// <c>TasteWeights</c> computes depth, completion ratio and engagement per series and reduces all
/// three to one number for the recommender. The Stats page reports totals and rankings. Neither
/// ever says whether somebody finishes what they start, how fast they read, or where they give up,
/// which is what this answers.
/// </para>
///
/// <para>
/// Needs no catalogue and no index: it is entirely about the reader, so it works on an install with
/// no MangaBaka database at all.
/// </para>
/// </summary>
public class ReadingBehaviourService(
    IServiceScopeFactory scopeFactory,
    IUserSettingsStore userSettings,
    ILogger<ReadingBehaviourService> logger)
{
    /// <summary>Timed chapters a series needs before its pace is worth naming.</summary>
    private const int MinTimedForPace = 4;

    /// <summary>Timed chapters overall before a median is worth reporting at all.</summary>
    private const int MinTimedOverall = 10;

    /// <summary>Series named in each of the three lists.</summary>
    private const int Named = 3;

    /// <summary>
    /// How far in somebody has to have got before stopping counts as abandoning rather than as not
    /// having started. One chapter of forty is a sample, not a verdict.
    /// </summary>
    private const double MinProgressToAbandon = 0.1;

    private const int CacheSlots = 40;
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(30);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<int, (ReadingBehaviour Behaviour, DateTime GeneratedAt)> _cache = [];

    public async Task<ReadingBehaviour> GetAsync(
        ICurrentUser scope, bool refresh, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!refresh &&
                _cache.TryGetValue(scope.UserId, out var hit) &&
                DateTime.UtcNow - hit.GeneratedAt < CacheFor)
            {
                return hit.Behaviour;
            }

            var behaviour = await BuildAsync(scope, ct);
            _cache[scope.UserId] = (behaviour, DateTime.UtcNow);

            while (_cache.Count > CacheSlots)
            {
                _cache.Remove(_cache.MinBy(kv => kv.Value.GeneratedAt).Key);
            }

            return behaviour;
        }
        finally
        {
            _lock.Release();
        }
    }

    private sealed record ProgressRow(int SeriesId, int ReadSeconds, int PageCount, DateTime UpdatedAt);

    private async Task<ReadingBehaviour> BuildAsync(ICurrentUser scope, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var timeZone = await UserTimeZone.ResolveAsync(userSettings, scope.UserId, ct);

        List<ProgressRow> progress;
        Dictionary<int, int> downloaded;
        Dictionary<int, (string Title, string? CoverUrl)> titles;
        using (var dbScope = scopeFactory.CreateScope())
        {
            var db = dbScope.ServiceProvider.GetRequiredService<MakiDbContext>();
            db.Scope.SetUser(scope.UserId, scope.AllRootFolders);

            // Visible, non-incognito series only, resolved under the scoped query so root-folder
            // visibility applies; the progress read below bypasses filters and intersects with this.
            var visible = await db.Series
                .Where(s => s.Incognito != IncognitoMode.Full)
                .Select(s => new { s.Id, s.Title, s.CoverPath, s.LastMetadataRefresh })
                .ToListAsync(ct);
            var visibleIds = visible.Select(v => v.Id).ToHashSet();
            titles = visible.ToDictionary(
                v => v.Id,
                v => (v.Title, SeriesDto.CoverUrlFor(v.Id, v.CoverPath, v.LastMetadataRefresh)));

            // Watched chapters are excluded on the same rule ReadCounts.ReadFor uses: ticking off an
            // anime season is not reading, and it carries no time and no page count to measure.
            var rows = await db.ChapterProgress.IgnoreQueryFilters()
                .Where(p => p.UserId == scope.UserId && p.Completed && !p.Watched)
                .Select(p => new ProgressRow(p.SeriesId, p.ReadSeconds, p.PageCount, p.UpdatedAt))
                .ToListAsync(ct);
            progress = [.. rows.Where(r => visibleIds.Contains(r.SeriesId))];

            downloaded = await db.Chapters.IgnoreQueryFilters()
                .Where(c => visibleIds.Contains(c.SeriesId) && c.ChapterFileId != null)
                .GroupBy(c => c.SeriesId)
                .Select(g => new { SeriesId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.SeriesId, x => x.Count, ct);
        }

        if (progress.Count == 0)
        {
            return new ReadingBehaviour(
                0, 0, null, null, null, 0, 0, 0, null, null, null, [], [], [], DateTime.UtcNow);
        }

        var bySeries = progress.GroupBy(p => p.SeriesId).ToList();

        // ---- finishing ----
        var completion = new List<(int SeriesId, double Fraction, int Read)>();
        foreach (var group in bySeries)
        {
            var held = downloaded.GetValueOrDefault(group.Key);
            if (held <= 0)
            {
                continue; // read chapters that are no longer on disk: no denominator to judge by
            }

            completion.Add((group.Key, Math.Min(1.0, (double)group.Count() / held), group.Count()));
        }

        var seriesStarted = completion.Count;
        var seriesFinished = completion.Count(c => c.Fraction >= 1.0);
        var unfinished = completion.Where(c => c.Fraction < 1.0).ToList();

        // ---- pace ----
        // Zero seconds means unknown, not instant: Kavita imports and OPDS page fetches never carry
        // time. Treating those as fast reading would make every imported library look like a blur.
        var timed = progress.Where(p => p.ReadSeconds > 0).ToList();
        var paceBySeries = timed
            .GroupBy(p => p.SeriesId)
            .Where(g => g.Count() >= MinTimedForPace)
            .Select(g => (SeriesId: g.Key, Median: Median([.. g.Select(p => (double)p.ReadSeconds)])!.Value))
            .ToList();

        // ---- days ----
        // Imports are dropped here rather than everywhere: they say what was read but not when, so a
        // single import would otherwise become the reader's "biggest day" for ever.
        var days = progress
            .Where(p => p.PageCount > 0)
            .GroupBy(p => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(p.UpdatedAt, timeZone)))
            .Select(g => (Day: g.Key, Count: g.Count()))
            .ToList();
        var biggest = days.Count == 0 ? default : days.MaxBy(d => d.Count);

        var behaviour = new ReadingBehaviour(
            SeriesStarted: seriesStarted,
            SeriesFinished: seriesFinished,
            FinishRate: seriesStarted > 0 ? (double)seriesFinished / seriesStarted : null,
            MedianStopPoint: Median([.. unfinished
                .Where(u => u.Fraction >= MinProgressToAbandon)
                .Select(u => u.Fraction)]),
            MedianSecondsPerChapter: timed.Count >= MinTimedOverall
                ? Median([.. timed.Select(p => (double)p.ReadSeconds)])
                : null,
            TimedChapters: timed.Count,
            ChaptersRead: progress.Count,
            ReadingDays: days.Count,
            MedianChaptersPerReadingDay: Median([.. days.Select(d => (double)d.Count)]),
            BiggestDayCount: days.Count == 0 ? null : biggest.Count,
            BiggestDay: days.Count == 0 ? null : biggest.Day,
            Savoured: Name(paceBySeries.OrderByDescending(p => p.Median), titles, Minutes),
            Devoured: Name(paceBySeries.OrderBy(p => p.Median), titles, Minutes),
            Abandoned: Name(
                unfinished.Where(u => u.Fraction >= MinProgressToAbandon)
                    .OrderByDescending(u => u.Read)
                    .Select(u => (u.SeriesId, Median: u.Fraction)),
                titles,
                f => $"{Math.Round(f * 100)}% in"),
            GeneratedAt: DateTime.UtcNow);

        logger.LogInformation(
            "Built reading behaviour over {Chapters} chapter(s) in {Elapsed:F1}s",
            progress.Count, (DateTime.UtcNow - started).TotalSeconds);

        return behaviour;
    }

    private static string Minutes(double seconds) =>
        seconds >= 90
            ? $"{Math.Round(seconds / 60)} min"
            : $"{Math.Round(seconds)} s";

    private static IReadOnlyList<BehaviourSeries> Name(
        IEnumerable<(int SeriesId, double Median)> ranked,
        Dictionary<int, (string Title, string? CoverUrl)> titles,
        Func<double, string> format) =>
        [.. ranked
            .Where(r => titles.ContainsKey(r.SeriesId))
            .Take(Named)
            .Select(r => new BehaviourSeries(
                r.SeriesId, titles[r.SeriesId].Title, titles[r.SeriesId].CoverUrl, format(r.Median)))];

    /// <summary>Median, or null for an empty set. Even counts take the mean of the middle pair.</summary>
    private static double? Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
    }
}
