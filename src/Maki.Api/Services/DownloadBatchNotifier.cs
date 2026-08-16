using Maki.Core.Entities;
using Maki.Core.Inbox;
using Maki.Core.Notifications;

namespace Maki.Api.Services;

/// <summary>
/// Collapses a burst of chapter downloads for one series into two notifications instead of one
/// per chapter: a "queued" ping carrying the count, and a summary once every item in the batch has
/// settled (including how many failed). Adding a series with 200 chapters otherwise sent 200 pings.
/// <para>
/// A batch is opened by whoever enqueues (<see cref="Queued"/>) and owns the queue item ids it was
/// given; <see cref="ChapterDownloadProcessor"/> reports each item's terminal outcome back and
/// suppresses its own per-chapter notification when the batch claims the item. Anything outside a
/// batch — a single manual chapter search, a retry of an item whose batch already closed — still
/// notifies per chapter, which is why a batch needs at least <see cref="MinBatchSize"/> items.
/// </para>
/// <para>
/// State is in-memory and per-process: a restart mid-batch loses the summary (the download itself
/// is recovered by <c>DownloadWorkerHostedService</c>). <see cref="SweepStale"/> closes batches
/// that stopped reporting so a leaked one can't silence a series' notifications forever.
/// </para>
/// </summary>
public sealed class DownloadBatchNotifier : IDisposable
{
    /// <summary>Below this, notify per chapter as before — one chapter needs no summary.</summary>
    private const int MinBatchSize = 2;

    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    private readonly NotificationService _notifications;
    private readonly InboxService _inbox;
    private readonly TimeProvider _time;
    private readonly ILogger<DownloadBatchNotifier> _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<int, Batch> _batches = [];
    private readonly ITimer _sweeper;

    public DownloadBatchNotifier(
        NotificationService notifications,
        InboxService inbox,
        TimeProvider time,
        ILogger<DownloadBatchNotifier> logger)
    {
        _notifications = notifications;
        _inbox = inbox;
        _time = time;
        _logger = logger;
        _sweeper = time.CreateTimer(_ => SweepStale(), null, SweepInterval, SweepInterval);
    }

    /// <summary>
    /// Opens a batch over the queue items just enqueued for a series, announcing the count. If a
    /// batch is already open for the series the items join it silently — a second "queued" ping for
    /// the same download run is noise, and the summary counts everything either way.
    /// </summary>
    /// <param name="origin">
    /// What queued these. Recorded on the batch so the summary knows whether an in-app notification
    /// is warranted: an automatic download is news, one somebody clicked is not. A batch that is
    /// joined by items of a different origin keeps the origin it opened with — mixing the two is a
    /// race that resolves either way, and the first one through is as good an answer as any.
    /// </param>
    /// <param name="announce">
    /// False when the caller already sent its own "queued" message (see
    /// <c>RefreshMonitoredSeriesJob</c>, which announces new chapters under
    /// <see cref="NotificationEventType.NewChapterAvailable"/>).
    /// </param>
    public void Queued(
        int seriesId,
        string seriesTitle,
        IReadOnlyCollection<int> queueItemIds,
        DownloadOrigin origin = DownloadOrigin.Unknown,
        bool announce = true)
    {
        if (queueItemIds.Count == 0)
        {
            return;
        }

        bool opened;
        lock (_lock)
        {
            if (!_batches.TryGetValue(seriesId, out var batch))
            {
                if (queueItemIds.Count < MinBatchSize)
                {
                    return;
                }

                batch = new Batch { Title = seriesTitle, Origin = origin };
                _batches[seriesId] = batch;
                opened = true;
            }
            else
            {
                opened = false;
            }

            batch.Pending.UnionWith(queueItemIds);
            batch.Queued += queueItemIds.Count;
            batch.LastActivity = _time.GetUtcNow();
        }

        if (!opened || !announce)
        {
            return;
        }

        _notifications.Dispatch(NotificationEventType.ChapterDownloaded, new NotificationMessage(
            NotificationEventType.ChapterDownloaded,
            Title: "Downloads queued",
            Body: $"{seriesTitle}: {queueItemIds.Count} chapter(s) queued for download",
            SeriesTitle: seriesTitle,
            SeriesId: seriesId));

        // Smart Download is the one queueing origin worth its own inbox event: nobody asked for it,
        // so "your library just started fetching this" is the whole point. The others either already
        // announced themselves (monitor refresh, via NewChapterAvailable) or were a click.
        if (origin == DownloadOrigin.SmartDownload)
        {
            _inbox.RaiseForSeries(InboxEventType.SmartDownloadQueued, new InboxMessage(
                Title: "Smart Download queued chapters",
                Body: $"{seriesTitle}: {queueItemIds.Count} chapter(s) queued to stay ahead of your reading",
                SeriesId: seriesId,
                Url: $"/series/{seriesId}"), seriesId);
        }
    }

    /// <summary>Records a finished download. True if a batch owns the item and the caller should stay quiet.</summary>
    public bool Completed(int seriesId, int queueItemId) => Report(seriesId, queueItemId, error: null);

    /// <summary>Records a failed download. True if a batch owns the item and the caller should stay quiet.</summary>
    public bool Failed(int seriesId, int queueItemId, string error) => Report(seriesId, queueItemId, error);

    /// <summary>
    /// Drops an item that will never report an outcome (cancelled or removed from the queue) so it
    /// can't hold its batch open. Closes and summarizes the batch if it was the last one pending.
    /// </summary>
    public void Discard(int seriesId, int queueItemId)
    {
        Batch? finished;
        lock (_lock)
        {
            if (!_batches.TryGetValue(seriesId, out var batch) || !batch.Pending.Remove(queueItemId))
            {
                return;
            }

            batch.Cancelled++;
            batch.LastActivity = _time.GetUtcNow();
            finished = Close(seriesId, batch);
        }

        if (finished is not null)
        {
            Summarize(seriesId, finished);
        }
    }

    private bool Report(int seriesId, int queueItemId, string? error)
    {
        Batch? finished;
        lock (_lock)
        {
            if (!_batches.TryGetValue(seriesId, out var batch) || !batch.Pending.Remove(queueItemId))
            {
                return false;
            }

            if (error is null)
            {
                batch.Completed++;
            }
            else
            {
                batch.Failed++;
                batch.FirstError ??= error;
            }

            batch.LastActivity = _time.GetUtcNow();
            finished = Close(seriesId, batch);
        }

        if (finished is not null)
        {
            Summarize(seriesId, finished);
        }

        return true;
    }

    /// <summary>Removes the batch if nothing is left pending. Caller must hold the lock.</summary>
    private Batch? Close(int seriesId, Batch batch)
    {
        if (batch.Pending.Count > 0)
        {
            return null;
        }

        _batches.Remove(seriesId);
        return batch;
    }

    /// <summary>
    /// Closes batches that stopped reporting outcomes. Every terminal state reports immediately
    /// (a failure is reported when it fails, not when its retries run out) and the longest an item
    /// can legitimately stall is a rate-limit cooldown, so an hour of silence means the batch leaked.
    /// </summary>
    internal void SweepStale()
    {
        List<(int SeriesId, Batch Batch)> stale;
        lock (_lock)
        {
            var cutoff = _time.GetUtcNow() - StaleAfter;
            stale = _batches
                .Where(kv => kv.Value.LastActivity <= cutoff)
                .Select(kv => (kv.Key, kv.Value))
                .ToList();

            foreach (var (seriesId, _) in stale)
            {
                _batches.Remove(seriesId);
            }
        }

        foreach (var (seriesId, batch) in stale)
        {
            _logger.LogWarning(
                "Download batch for series {SeriesId} went quiet with {Pending} item(s) unfinished; closing it",
                seriesId, batch.Pending.Count);
            Summarize(seriesId, batch);
        }
    }

    private void Summarize(int seriesId, Batch batch)
    {
        var unfinished = batch.Pending.Count;
        var automatic = batch.Origin is
            DownloadOrigin.SmartDownload or DownloadOrigin.MonitorRefresh or DownloadOrigin.RequestApproval;

        if (batch.Failed == 0 && unfinished == 0)
        {
            _notifications.Dispatch(NotificationEventType.ChapterDownloaded, new NotificationMessage(
                NotificationEventType.ChapterDownloaded,
                Title: "Downloads complete",
                Body: $"{batch.Title}: {batch.Completed} of {batch.Queued} chapter(s) downloaded",
                SeriesTitle: batch.Title,
                SeriesId: seriesId));

            if (automatic)
            {
                _inbox.RaiseForSeries(InboxEventType.ChapterDownloaded, new InboxMessage(
                    Title: "New chapters downloaded",
                    Body: $"{batch.Title}: {batch.Completed} chapter(s) ready to read",
                    SeriesId: seriesId,
                    Url: $"/series/{seriesId}"), seriesId);
            }

            return;
        }

        var parts = new List<string> { $"{batch.Completed} downloaded" };
        if (batch.Failed > 0)
        {
            parts.Add($"{batch.Failed} failed");
        }

        if (batch.Cancelled > 0)
        {
            parts.Add($"{batch.Cancelled} cancelled");
        }

        if (unfinished > 0)
        {
            parts.Add($"{unfinished} unfinished");
        }

        var body = $"{batch.Title}: {string.Join(", ", parts)} of {batch.Queued} queued";
        if (batch.FirstError is { } firstError)
        {
            body += $". First error: {firstError}";
        }

        // Routed to the failure toggle: a run that lost chapters is what that toggle is for, and it
        // replaces the per-chapter failure pings the processor would otherwise have sent.
        var level = batch.Completed > 0 ? NotificationLevel.Warning : NotificationLevel.Error;
        _notifications.Dispatch(NotificationEventType.DownloadFailed, new NotificationMessage(
            NotificationEventType.DownloadFailed,
            Title: "Downloads finished with errors",
            Body: body,
            Level: level,
            SeriesTitle: batch.Title,
            SeriesId: seriesId));

        if (automatic)
        {
            _inbox.RaiseForSeries(InboxEventType.DownloadFailed, new InboxMessage(
                Title: "Downloads finished with errors",
                Body: body,
                Level: level,
                SeriesId: seriesId,
                Url: $"/series/{seriesId}"), seriesId);
        }
    }

    public void Dispose() => _sweeper.Dispose();

    private sealed class Batch
    {
        public required string Title { get; init; }

        /// <summary>What opened the batch. Decides whether the summary is inbox-worthy.</summary>
        public DownloadOrigin Origin { get; init; }

        public HashSet<int> Pending { get; } = [];
        public int Queued { get; set; }
        public int Completed { get; set; }
        public int Failed { get; set; }
        public int Cancelled { get; set; }
        public string? FirstError { get; set; }
        public DateTimeOffset LastActivity { get; set; }
    }
}
