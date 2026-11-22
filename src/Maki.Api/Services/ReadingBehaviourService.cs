using Maki.Api.Dtos;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>A series named as an example of how the reader reads, not of what they read.</summary>
/// <param name="Measure">
/// Median seconds per chapter in
/// <see cref="ReadingBehaviour.Savoured"/> and <see cref="ReadingBehaviour.Devoured"/>, the completion
/// fraction (0 to 1) in <see cref="ReadingBehaviour.Abandoned"/>.
/// </param>
public record BehaviourSeries(int SeriesId, string Title, string? CoverUrl, double Measure);

/// <summary>
/// How long the series a reader finishes are, against the ones they stall on. Length is the
/// series' known chapter count where the catalogue has one, else what is on disk.
/// </summary>
public record LengthSweetSpot(double Finished, double Stalled, int FinishedCount, int StalledCount);

/// <summary>One series' reading, by the days it was read on.</summary>
/// <param name="PerDay">Chapters finished on each day the series was read, in order. Days without
/// reading are left out, so a two-day binge is two entries however long ago it started.</param>
public record BingeExample(
    int SeriesId, string Title, string? CoverUrl, int Chapters, int ActiveDays, int SpanDays,
    IReadOnlyList<int> PerDay);

/// <summary>
/// Whether a reader binges a series or follows it: of the series with enough dated chapters, how
/// many were read in one or two days.
/// </summary>
public record BingeHabit(int Series, int Binged, IReadOnlyList<BingeExample> Examples);

/// <summary>Pace within one reading format, with its own slow and fast series.</summary>
/// <param name="Format"><c>paged</c> or <c>strip</c> (manhwa and manhua).</param>
public record FormatPace(
    string Format, double MedianSeconds, int TimedChapters,
    IReadOnlyList<BehaviourSeries> Savoured, IReadOnlyList<BehaviourSeries> Devoured);

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
/// <param name="StopPointHistogram">
/// Unfinished series by how far in they are, ten buckets of a tenth each (bucket 0 is under 10%).
/// Unlike <paramref name="MedianStopPoint"/> this keeps the barely-sampled ones, so the chart shows
/// the whole shape.
/// </param>
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
    DateTime GeneratedAt,
    IReadOnlyList<int> StopPointHistogram,
    LengthSweetSpot? Length = null,
    BingeHabit? Binge = null,
    IReadOnlyList<FormatPace>? PaceByFormat = null);

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

    /// <summary>
    /// Days without a read before a part-read series counts as stalled for the length comparison,
    /// so a series somebody is in the middle of this week is not set against the ones they finish.
    /// </summary>
    private const int StalledAfterDays = 30;

    private const int HistogramBuckets = 10;

    /// <summary>Finished and stalled series each needed before the two lengths are compared.</summary>
    private const int MinLengthSide = 2;

    /// <summary>Dated chapters a series needs before how it was read says anything.</summary>
    private const int MinBingeChapters = 5;

    /// <summary>Series with enough dated chapters before binge-or-follow is offered.</summary>
    private const int MinBingeSeries = 3;

    /// <summary>Reading days at or under which a series counts as binged.</summary>
    private const int BingeDays = 2;

    /// <summary>Series drawn as day strips: the biggest binges and one series read over many days.</summary>
    private const int BingeExamples = 6;


    /// <summary>Timed chapters each format needs before the split is offered.</summary>
    private const int MinTimedPerFormat = 10;

    private const int CacheSlots = 40;
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(30);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<(int UserId, string Access), (ReadingBehaviour Behaviour, DateTime GeneratedAt)> _cache = [];

    /// <summary>
    /// Which series a user can see, as a cache-key fragment. Part of the key so a changed grant misses
    /// the cache instead of serving titles from a folder the user has since lost.
    /// </summary>
    internal static string AccessKey(bool allRootFolders, IEnumerable<int> rootFolderIds) =>
        allRootFolders ? "*" : string.Join(',', rootFolderIds.Order());

    public async Task<string> AccessKeyAsync(int userId, bool allRootFolders, CancellationToken ct)
    {
        if (allRootFolders)
        {
            return AccessKey(true, []);
        }

        using var dbScope = scopeFactory.CreateScope();
        var db = dbScope.ServiceProvider.GetRequiredService<MakiDbContext>();
        var folders = await db.UserRootFolders.AsNoTracking().IgnoreQueryFilters()
            .Where(g => g.UserId == userId)
            .Select(g => g.RootFolderId)
            .ToListAsync(ct);
        return AccessKey(false, folders);
    }

    public Task<ReadingBehaviour> GetAsync(
        ICurrentUser scope, bool refresh, CancellationToken ct = default) =>
        GetAsync(scope.UserId, scope.AllRootFolders, refresh, ct);

    /// <summary>
    /// For a named user, as the stats page's admin view needs. <paramref name="allRootFolders"/> and
    /// that user's own folder grants decide which series count, same as for a signed-in caller.
    /// </summary>
    public async Task<ReadingBehaviour> GetAsync(
        int userId, bool allRootFolders, bool refresh, CancellationToken ct = default)
    {
        var key = (userId, await AccessKeyAsync(userId, allRootFolders, ct));
        await _lock.WaitAsync(ct);
        try
        {
            if (!refresh &&
                _cache.TryGetValue(key, out var hit) &&
                DateTime.UtcNow - hit.GeneratedAt < CacheFor)
            {
                return hit.Behaviour;
            }

            var behaviour = await BuildAsync(userId, allRootFolders, ct);
            _cache[key] = (behaviour, DateTime.UtcNow);

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

    private sealed record ProgressRow(int SeriesId, int ReadSeconds, int PageCount, DateTime FinishedAt, bool BulkMarked);

    private sealed record SeriesInfo(string Title, string? CoverUrl, string? Type, int? TotalChapters);

    private async Task<ReadingBehaviour> BuildAsync(int userId, bool allRootFolders, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var timeZone = await UserTimeZone.ResolveAsync(userSettings, userId, ct);

        List<ProgressRow> progress;
        Dictionary<int, int> downloaded;
        Dictionary<int, (string Title, string? CoverUrl)> titles;
        Dictionary<int, SeriesInfo> info;
        using (var dbScope = scopeFactory.CreateScope())
        {
            var db = dbScope.ServiceProvider.GetRequiredService<MakiDbContext>();
            db.Scope.SetUser(userId, allRootFolders);

            // Visible, non-incognito series only, resolved under the scoped query so root-folder
            // visibility applies; the progress read below bypasses filters and intersects with this.
            var visible = await db.Series
                .Where(s => s.Incognito != IncognitoMode.Full)
                .Select(s => new { s.Id, s.Title, s.CoverPath, s.LastMetadataRefresh, s.Type, s.TotalChapters })
                .ToListAsync(ct);
            var visibleIds = visible.Select(v => v.Id).ToHashSet();
            titles = visible.ToDictionary(
                v => v.Id,
                v => (v.Title, SeriesDto.CoverUrlFor(v.Id, v.CoverPath, v.LastMetadataRefresh)));
            info = visible.ToDictionary(
                v => v.Id,
                v => new SeriesInfo(v.Title, titles[v.Id].CoverUrl, v.Type, v.TotalChapters));

            // Watched chapters are excluded on the same rule ReadCounts.ReadFor uses: ticking off an
            // anime season is not reading, and it carries no time and no page count to measure.
            var rows = await db.ChapterProgress.IgnoreQueryFilters()
                .Where(p => p.UserId == userId && p.Completed && !p.Watched)
                .Select(p => new ProgressRow(p.SeriesId, p.ReadSeconds, p.PageCount, p.CompletedAt ?? p.UpdatedAt, p.BulkMarked))
                .ToListAsync(ct);
            progress = [.. rows.Where(r => visibleIds.Contains(r.SeriesId))];

            downloaded = await db.Chapters.IgnoreQueryFilters()
                .Where(c => visibleIds.Contains(c.SeriesId) && (c.ChapterFileId != null || c.FileRemovedAt != null))
                .GroupBy(c => c.SeriesId)
                .Select(g => new { SeriesId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.SeriesId, x => x.Count, ct);
        }

        if (progress.Count == 0)
        {
            return new ReadingBehaviour(
                0, 0, null, null, null, 0, 0, 0, null, null, null, [], [], [], DateTime.UtcNow,
                new int[HistogramBuckets]);
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
        var histogram = new int[HistogramBuckets];
        foreach (var u in unfinished)
        {
            histogram[Math.Min(HistogramBuckets - 1, (int)(u.Fraction * HistogramBuckets))]++;
        }

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
        // Imports and bulk "mark read" ticks are dropped here rather than everywhere: they say what
        // was read but not when, so a single tick would otherwise become the reader's "biggest day"
        // for ever. Days are dated by first completion, which a re-read leaves alone. OPDS reads
        // carry no time but are real, so they count.
        var days = progress
            .Where(p => p.PageCount > 0 && !p.BulkMarked)
            .GroupBy(p => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(p.FinishedAt, timeZone)))
            .Select(g => (Day: g.Key, Count: g.Count()))
            .ToList();
        var biggest = days.Count == 0 ? default : days.MaxBy(d => d.Count);

        var pooled = SlowAndFast(paceBySeries, titles);
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
            Savoured: pooled.Slow,
            Devoured: pooled.Fast,
            Abandoned: Name(
                unfinished.Where(u => u.Fraction >= MinProgressToAbandon)
                    .OrderByDescending(u => u.Read)
                    .Select(u => (u.SeriesId, Median: u.Fraction)),
                titles),
            GeneratedAt: DateTime.UtcNow,
            StopPointHistogram: histogram,
            Length: Length(completion, downloaded, info,
                bySeries.ToDictionary(g => g.Key, g => g.Max(p => p.FinishedAt)), DateTime.UtcNow),
            Binge: Binge(progress, timeZone, info),
            PaceByFormat: PaceByFormat(timed, paceBySeries, info, titles));

        logger.LogInformation(
            "Built reading behaviour over {Chapters} chapter(s) in {Elapsed:F1}s",
            progress.Count, (DateTime.UtcNow - started).TotalSeconds);

        return behaviour;
    }

    /// <summary>
    /// Median length of the finished series against the stalled ones. A series' length is the larger
    /// of what is on disk and the catalogue's count, and both sides are judged against it, not
    /// against the disk alone: three downloaded chapters of a two-hundred-chapter run, all read, are
    /// a sample, not a finished long series. Stalled is read at least
    /// <see cref="MinProgressToAbandon"/> of that length and untouched for
    /// <see cref="StalledAfterDays"/>, so a series being read right now is on neither side.
    /// </summary>
    private static LengthSweetSpot? Length(
        List<(int SeriesId, double Fraction, int Read)> completion,
        Dictionary<int, int> downloaded,
        Dictionary<int, SeriesInfo> info,
        Dictionary<int, DateTime> lastRead,
        DateTime now)
    {
        double LengthOf(int id) =>
            Math.Max(downloaded.GetValueOrDefault(id), info.TryGetValue(id, out var i) ? i.TotalChapters ?? 0 : 0);

        var measured = completion
            .Select(c => (c.SeriesId, c.Read, Length: LengthOf(c.SeriesId)))
            .Where(c => c.Length > 0)
            .Select(c => (c.SeriesId, c.Length, Share: c.Read / c.Length))
            .ToList();
        var finished = measured.Where(c => c.Share >= 1.0).Select(c => c.Length).ToList();
        var stalled = measured
            .Where(c => c.Share >= MinProgressToAbandon && c.Share < 1.0 &&
                now - lastRead.GetValueOrDefault(c.SeriesId, now) >= TimeSpan.FromDays(StalledAfterDays))
            .Select(c => c.Length).ToList();
        if (finished.Count < MinLengthSide || stalled.Count < MinLengthSide)
        {
            return null;
        }

        return new LengthSweetSpot(Median(finished)!.Value, Median(stalled)!.Value, finished.Count, stalled.Count);
    }

    /// <summary>
    /// How many series were read in one or two days, over the series with
    /// <see cref="MinBingeChapters"/> dated chapters. Imports and bulk ticks carry no real date, so
    /// they are left out as the reading days are. The examples are the three biggest binges and,
    /// when there is one, the series read over the most days, so the strip shows both shapes.
    /// </summary>
    private static BingeHabit? Binge(List<ProgressRow> progress, TimeZoneInfo timeZone, Dictionary<int, SeriesInfo> info)
    {
        var series = progress
            .Where(p => p.PageCount > 0 && !p.BulkMarked && info.ContainsKey(p.SeriesId))
            .GroupBy(p => p.SeriesId)
            .Where(g => g.Count() >= MinBingeChapters)
            .Select(g =>
            {
                var days = g.Select(p => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(p.FinishedAt, timeZone)))
                    .ToList();
                var first = days.Min();
                var span = days.Max().DayNumber - first.DayNumber + 1;
                return (SeriesId: g.Key, Days: days, First: first, Span: span, Active: days.Distinct().Count());
            })
            .ToList();
        if (series.Count < MinBingeSeries)
        {
            return null;
        }

        // By calendar span, not reading days: three chapters in January and three in June is two
        // reading days and no binge.
        var binged = series.Where(x => x.Span <= BingeDays).ToList();
        var picks = binged.OrderByDescending(x => x.Days.Count).ThenBy(x => x.SeriesId).Take(BingeExamples - 1).ToList();
        var spread = series.Where(x => x.Span > BingeDays)
            .OrderByDescending(x => x.Active).ThenBy(x => x.SeriesId).FirstOrDefault();
        if (spread.Days is not null)
        {
            picks.Add(spread);
        }

        return new BingeHabit(
            series.Count,
            binged.Count,
            [.. picks.Select(x =>
            {
                var perDay = x.Days.GroupBy(d => d).OrderBy(g => g.Key).Select(g => g.Count()).ToList();
                var s = info[x.SeriesId];
                return new BingeExample(x.SeriesId, s.Title, s.CoverUrl, x.Days.Count, x.Active, x.Span, perDay);
            })]);
    }

    /// <summary>
    /// Pace split into paged and long-strip reading, with each format's own slow and fast series.
    /// A long-strip chapter is one tall page and takes longer than a twenty-page manga chapter, so
    /// pooled lists name strips as "slow" and manga as "fast" whatever the reader's pace. Only
    /// offered when both formats have <see cref="MinTimedPerFormat"/> timed chapters; the cut is the
    /// one <see cref="ReadingTimeEstimateService"/> uses.
    /// </summary>
    private static IReadOnlyList<FormatPace>? PaceByFormat(
        List<ProgressRow> timed,
        List<(int SeriesId, double Median)> paceBySeries,
        Dictionary<int, SeriesInfo> info,
        Dictionary<int, (string Title, string? CoverUrl)> titles)
    {
        bool IsStrip(int seriesId) =>
            info.TryGetValue(seriesId, out var i) && (i.Type == SeriesTypes.Manhwa || i.Type == SeriesTypes.Manhua);

        var formats = new[] { ("paged", false), ("strip", true) }
            .Select(f =>
            {
                var chapters = timed.Where(p => IsStrip(p.SeriesId) == f.Item2).Select(p => (double)p.ReadSeconds).ToList();
                var series = paceBySeries.Where(p => IsStrip(p.SeriesId) == f.Item2).ToList();
                return (Format: f.Item1, Chapters: chapters, Series: series);
            })
            .ToList();
        if (formats.Any(f => f.Chapters.Count < MinTimedPerFormat))
        {
            return null;
        }

        return [.. formats.Select(f =>
        {
            var (slow, fast) = SlowAndFast(f.Series, titles);
            return new FormatPace(f.Format, Median(f.Chapters)!.Value, f.Chapters.Count, slow, fast);
        })];
    }

    /// <summary>
    /// The series read slowest and fastest, never the same one in both. With fewer than twice
    /// <see cref="Named"/> series the slower half goes to the first list and the rest to the second,
    /// so a format with four timed series names two of each rather than one series twice.
    /// </summary>
    private static (IReadOnlyList<BehaviourSeries> Slow, IReadOnlyList<BehaviourSeries> Fast) SlowAndFast(
        IEnumerable<(int SeriesId, double Median)> paced,
        Dictionary<int, (string Title, string? CoverUrl)> titles)
    {
        var ranked = paced.Where(p => titles.ContainsKey(p.SeriesId))
            .OrderByDescending(p => p.Median).ThenBy(p => p.SeriesId).ToList();
        var slowCount = Math.Min(Named, (ranked.Count + 1) / 2);
        var fastCount = Math.Min(Named, ranked.Count - slowCount);
        var slow = Name(ranked.Take(slowCount), titles);
        var fast = Name(Enumerable.Reverse(ranked).Take(fastCount), titles);
        return (slow, fast);
    }

    private static IReadOnlyList<BehaviourSeries> Name(
        IEnumerable<(int SeriesId, double Median)> ranked,
        Dictionary<int, (string Title, string? CoverUrl)> titles) =>
        [.. ranked
            .Where(r => titles.ContainsKey(r.SeriesId))
            .Take(Named)
            .Select(r => new BehaviourSeries(
                r.SeriesId, titles[r.SeriesId].Title, titles[r.SeriesId].CoverUrl, r.Median))];

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
