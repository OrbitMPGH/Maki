namespace Maki.Core.Entities;

/// <summary>
/// What put an item on the queue. Recorded on the row rather than inferred, because every enqueue
/// path funnels through <c>DownloadQueueService.EnqueueChapterAsync</c> and is indistinguishable
/// afterwards, and because the answer has to survive a restart mid-download.
/// <para>
/// The in-app notification inbox is the consumer: a download somebody triggered by hand needs no
/// notification (they watched themselves click it), one that happened on its own does.
/// </para>
/// </summary>
public enum DownloadOrigin
{
    /// <summary>Rows written before the column existed. Treated as manual — never notifies.</summary>
    Unknown = 0,

    /// <summary>Somebody clicked search or download on a series or chapter.</summary>
    Manual = 1,

    SmartDownload = 2,
    MonitorRefresh = 3,

    /// <summary>Queued by an admin approving somebody else's request; the requester is the one to tell.</summary>
    RequestApproval = 4,

    // No "Retry" member: retrying reuses the original row rather than enqueueing a new one, so a
    // retried automatic download keeps its origin and still reports when it finally succeeds.
}

public class DownloadQueueItem
{
    public int Id { get; set; }

    public int SeriesId { get; set; }
    public Series? Series { get; set; }

    /// <summary>Null for release grabs, which are series-level (one torrent can span many chapters).</summary>
    public int? ChapterId { get; set; }
    public Chapter? Chapter { get; set; }

    /// <summary>Null for items acquired via indexer releases instead of a scraper.</summary>
    public int? SourceMappingId { get; set; }
    public SourceMapping? SourceMapping { get; set; }

    /// <summary>
    /// The source's own chapter id, resolved once at enqueue time (<c>ChapterSourceResolver</c>)
    /// so the worker doesn't need to re-list the source's chapters before every download. Null for
    /// torrent items. Re-resolved and overwritten if it 404s by the time the item is actually dispatched.
    /// </summary>
    public string? SourceChapterId { get; set; }

    public AcquisitionProtocol Protocol { get; set; } = AcquisitionProtocol.Scraper;

    /// <summary>Serialized ReleaseInfo for torrent/usenet acquisitions.</summary>
    public string? ReleaseInfoJson { get; set; }

    /// <summary>Release title shown in the queue for series-level grabs.</summary>
    public string? Title { get; set; }

    public QueueStatus Status { get; set; } = QueueStatus.Queued;
    public int PagesTotal { get; set; }
    public int PagesDone { get; set; }
    public int RetryCount { get; set; }
    public string? ErrorMessage { get; set; }

    public DateTime QueuedAt { get; set; }
    public DateTime? NextAttempt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>Manual position within the active queue. Lower dispatches first; ties break on <see cref="QueuedAt"/>.</summary>
    public int SortOrder { get; set; }

    /// <summary>What queued this. See <see cref="DownloadOrigin"/>.</summary>
    public DownloadOrigin Origin { get; set; }

    /// <summary>
    /// Who the download is for, when that is a specific person: the requester behind a
    /// <see cref="DownloadOrigin.RequestApproval"/>, or whoever clicked a manual search. Null for the
    /// scheduled jobs, whose work is on behalf of everyone reading the series. Not a foreign key —
    /// a deleted account should not take the queue history with it.
    /// </summary>
    public int? QueuedByUserId { get; set; }

    /// <summary>Whether an inbox notification is warranted when this item settles.</summary>
    public bool IsAutomatic => Origin is
        DownloadOrigin.SmartDownload or DownloadOrigin.MonitorRefresh or DownloadOrigin.RequestApproval;
}
