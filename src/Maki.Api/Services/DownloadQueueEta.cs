using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <param name="Seconds">Time left until the series' last queued chapter is done, or null when it cannot be told.</param>
/// <param name="Held">Every row of the series is held back by a pause or a low disk.</param>
public sealed record QueueSeriesEtaDto(int SeriesId, double? Seconds, bool Held);

/// <param name="QueueSeconds">Time left for everything that is not held, or null when it cannot be told.</param>
/// <param name="Held">Nothing can start at all: a pause on every download.</param>
public sealed record QueueEtaDto(double? QueueSeconds, IReadOnlyList<QueueSeriesEtaDto> Series, bool Held);

/// <summary>
/// A rough time left for the scraper queue, from how fast each source downloaded lately.
/// <para>
/// Each completed row with a <see cref="DownloadQueueItem.StartedAt"/> is one sample: its pages over the
/// time from the claim to the end. Per source that gives pages per minute for one worker, and the
/// overlap of its samples gives how many ran at once, which is as many as the estimate lets that
/// source run, since a site's rate limit is what usually caps it. The queue is then played out in claim
/// order over the configured number of workers. A source with fewer than <see cref="MinSourceSamples"/>
/// samples uses the pooled rate of every source; with fewer than <see cref="MinPooledSamples"/> in all
/// there is no estimate, rather than a made-up one.
/// </para>
/// </summary>
public class DownloadQueueEta(
    IServiceScopeFactory scopeFactory,
    TimeProvider time,
    IAppSettings settings,
    DownloadPauseService? pauses = null,
    DownloadDiskHold? diskHold = null)
{
    public const int MinSourceSamples = 3;
    public const int MinPooledSamples = 5;
    public static readonly TimeSpan HistoryWindow = TimeSpan.FromDays(7);
    private const int MaxSamples = 500;
    private const int DefaultWorkers = 2;
    private const int MaxWorkers = 8;

    /// <summary>One finished download: its source, pages and when it ran.</summary>
    public sealed record Sample(string? Source, int Pages, DateTime Start, DateTime End);

    /// <summary>One row still to download, in claim order.</summary>
    /// <param name="PagesLeft">Known pages still to fetch, or null before the source said how many.</param>
    /// <param name="NotBefore">A rate-limited row's next attempt, when that is still ahead.</param>
    public sealed record Work(int SeriesId, string? Source, int? PagesLeft, bool InFlight, DateTime? NotBefore);

    private sealed record Rate(double PagesPerMinute, double PagesPerChapter, int Parallel);

    /// <summary>
    /// The estimate for every row the request's <paramref name="db"/> can see. The play-out itself reads
    /// the whole queue, because rows in folders the caller cannot see still take workers.
    /// </summary>
    public async Task<QueueEtaDto> EstimateAsync(MakiDbContext db, CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var pause = pauses is null ? DownloadPauseState.None : await pauses.ActiveAsync(ct);
        var heldRoots = (diskHold?.HeldRootIds ?? []).ToHashSet();

        using var scope = scopeFactory.CreateScope();
        var all = scope.ServiceProvider.GetRequiredService<MakiDbContext>();

        var since = now - HistoryWindow;
        var samples = await all.DownloadQueue
            .Where(q => q.Protocol == AcquisitionProtocol.Scraper && q.Status == QueueStatus.Completed &&
                        q.StartedAt != null && q.CompletedAt != null && q.CompletedAt >= since && q.PagesTotal > 0)
            .OrderByDescending(q => q.CompletedAt)
            .Take(MaxSamples)
            .Select(q => new Sample(
                q.SourceMapping != null ? q.SourceMapping.SourceName : null, q.PagesTotal, q.StartedAt!.Value, q.CompletedAt!.Value))
            .ToListAsync(ct);

        var rows = await all.DownloadQueue
            .Where(q => q.Protocol == AcquisitionProtocol.Scraper &&
                        (q.Status == QueueStatus.Resolving || q.Status == QueueStatus.Queued ||
                         q.Status == QueueStatus.RateLimited || q.Status == QueueStatus.FetchingPages ||
                         q.Status == QueueStatus.Downloading || q.Status == QueueStatus.Validating ||
                         q.Status == QueueStatus.Packaging || q.Status == QueueStatus.Importing))
            .OrderBy(q => q.Priority)
            .ThenBy(q => q.SortOrder)
            .ThenBy(q => q.QueuedAt)
            .Select(q => new
            {
                q.SeriesId,
                q.Series!.RootFolderId,
                Source = q.SourceMapping != null ? q.SourceMapping.SourceName : null,
                q.Status,
                q.PagesTotal,
                q.PagesDone,
                q.NextAttempt,
            })
            .ToListAsync(ct);

        var work = new List<Work>();
        var heldSeries = new HashSet<int>();
        foreach (var row in rows)
        {
            var inFlight = row.Status is not (QueueStatus.Resolving or QueueStatus.Queued or QueueStatus.RateLimited);
            var held = !inFlight &&
                       (pause.All is not null || pause.PausedSeries.ContainsKey(row.SeriesId) ||
                        heldRoots.Contains(row.RootFolderId) ||
                        (row.Source is { } source && pause.Sources.ContainsKey(source)));
            if (held)
            {
                heldSeries.Add(row.SeriesId);
                continue;
            }

            work.Add(new Work(
                row.SeriesId, row.Source,
                row.PagesTotal > 0 ? Math.Max(0, row.PagesTotal - row.PagesDone) : null,
                inFlight,
                row.NextAttempt is { } next && next > now ? next : null));
        }

        var workers = await WorkersAsync(ct);
        var finish = Simulate(samples, work, workers, now);

        var queueSeconds = finish is null ? null : work.Count == 0 ? (double?)0 : finish.Values.DefaultIfEmpty(0).Max();
        var seriesIds = work.Select(w => w.SeriesId).Concat(heldSeries).Distinct().ToList();
        var visible = (await db.Series.Where(s => seriesIds.Contains(s.Id)).Select(s => s.Id).ToListAsync(ct)).ToHashSet();
        var series = seriesIds
            .Where(visible.Contains)
            .Select(id => finish is not null && finish.TryGetValue(id, out var seconds)
                ? new QueueSeriesEtaDto(id, seconds, false)
                : new QueueSeriesEtaDto(id, null, heldSeries.Contains(id) && work.All(w => w.SeriesId != id)))
            .ToList();
        return new QueueEtaDto(pause.All is not null ? null : queueSeconds, series, pause.All is not null);
    }

    private async Task<int> WorkersAsync(CancellationToken ct) =>
        int.TryParse(await settings.GetAsync(SettingKeys.DownloadConcurrentChapters, ct), out var configured)
            ? Math.Clamp(configured, 1, MaxWorkers)
            : DefaultWorkers;

    /// <summary>
    /// Plays <paramref name="work"/> out over <paramref name="workers"/> in the order given and answers
    /// the seconds from <paramref name="now"/> until each series' last row is done, or null when the
    /// history is too thin for any estimate.
    /// </summary>
    public static Dictionary<int, double>? Simulate(
        IReadOnlyList<Sample> samples, IReadOnlyList<Work> work, int workers, DateTime now)
    {
        var usable = samples.Where(s => s.End > s.Start && s.Pages > 0).ToList();
        if (usable.Count < MinPooledSamples)
        {
            return null;
        }

        workers = Math.Max(1, workers);
        var pooled = RateOf(usable, workers);
        var rates = usable
            .Where(s => s.Source is not null)
            .GroupBy(s => s.Source!)
            .Where(g => g.Count() >= MinSourceSamples)
            .ToDictionary(g => g.Key, g => RateOf(g.ToList(), workers));

        var free = new double[workers];
        var running = new Dictionary<string, List<double>>();
        var done = new Dictionary<int, double>();

        // Rows already downloading hold a worker from the start, so they go first whatever their place.
        foreach (var row in work.Where(w => w.InFlight).Concat(work.Where(w => !w.InFlight)))
        {
            var rate = row.Source is { } name && rates.TryGetValue(name, out var own) ? own : pooled;
            var pages = row.PagesLeft ?? rate.PagesPerChapter;
            var minutes = pages / rate.PagesPerMinute;

            var slot = Array.IndexOf(free, free.Min());
            var start = Math.Max(free[slot], row.NotBefore is { } at ? (at - now).TotalMinutes : 0);
            if (row.Source is { } source)
            {
                if (!running.TryGetValue(source, out var ends))
                {
                    running[source] = ends = [];
                }

                // No more of one source at once than it has been seen to run.
                ends.RemoveAll(end => end <= start);
                while (ends.Count >= rate.Parallel)
                {
                    start = Math.Max(start, ends.Min());
                    ends.RemoveAll(end => end <= start);
                }

                ends.Add(start + minutes);
            }

            free[slot] = start + minutes;
            done[row.SeriesId] = Math.Max(done.GetValueOrDefault(row.SeriesId), (start + minutes) * 60);
        }

        return done;
    }

    private static Rate RateOf(IReadOnlyList<Sample> samples, int workers)
    {
        var pages = samples.Sum(s => (double)s.Pages);
        var minutes = samples.Sum(s => (s.End - s.Start).TotalMinutes);
        var busy = BusyMinutes(samples);
        var parallel = busy > 0 ? (int)Math.Round(minutes / busy) : 1;
        return new Rate(pages / minutes, pages / samples.Count, Math.Clamp(parallel, 1, workers));
    }

    /// <summary>Minutes in which at least one of the samples was running.</summary>
    private static double BusyMinutes(IEnumerable<Sample> samples)
    {
        double total = 0;
        DateTime? from = null, to = null;
        foreach (var s in samples.OrderBy(s => s.Start))
        {
            if (to is { } end && s.Start <= end)
            {
                if (s.End > end) to = s.End;
                continue;
            }

            if (from is { } a && to is { } b) total += (b - a).TotalMinutes;
            from = s.Start;
            to = s.End;
        }

        if (from is { } x && to is { } y) total += (y - x).TotalMinutes;
        return total;
    }
}
