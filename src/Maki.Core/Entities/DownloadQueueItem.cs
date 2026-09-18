using System.Text.Json;

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
    HealthRepair = 5,

    // No "Retry" member: retrying reuses the original row rather than enqueueing a new one, so a
    // retried automatic download keeps its origin and still reports when it finally succeeds.
}

public class DownloadQueueItem
{
    public int Id { get; set; }
    public int? HealthOperationId { get; set; }

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

    /// <summary>
    /// The catalogue key for why this item is not moving, rendered by whoever is reading the queue
    /// rather than at the moment it failed. A SignalR queue update reaches every connected client at
    /// once and they do not share a language, so there is no one language to render it in here.
    /// <para>
    /// Null when the reason cannot be keyed at all, which today means text that came from outside
    /// Maki, and on rows written before the queue was keyed. <see cref="ErrorMessage"/> carries both
    /// of those.
    /// </para>
    /// </summary>
    public string? ErrorKey { get; set; }

    /// <summary>
    /// JSON object of the values filling the message's placeholders, or null when it has none.
    /// </summary>
    public string? ErrorParamsJson { get; set; }

    /// <summary>
    /// Free text, used when <see cref="ErrorKey"/> is null: an error another program worded (a
    /// torrent client, a scraper), and the English written by Maki itself before the queue was
    /// keyed. Not a fallback rendering of the key, which would just be English by another route.
    /// </summary>
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

    /// <summary>
    /// Record why this item stopped, as a catalogue key plus the values its placeholders need.
    /// <para>
    /// <paramref name="args"/> is an anonymous object: <c>SetError("error.download.rateLimited",
    /// new { source })</c>. Keep out of it anything the DTO already carries as its own field, such
    /// as the retry time: repeating it here freezes one rendering of it.
    /// </para>
    /// </summary>
    public void SetError(string key, object? args = null)
    {
        ErrorKey = key;
        ErrorParamsJson = args is null ? null : JsonSerializer.Serialize(args);
        ErrorMessage = null;
    }

    /// <summary>
    /// Record a reason Maki did not word: a scraper's or torrent client's own text. It is stored
    /// and shown as it arrived, because translating somebody else's error would mean parsing it.
    /// </summary>
    public void SetRawError(string? message)
    {
        ErrorKey = null;
        ErrorParamsJson = null;
        ErrorMessage = message;
    }

    /// <summary>No longer failing. Clears all three columns, not just the one that was set.</summary>
    public void ClearError()
    {
        ErrorKey = null;
        ErrorParamsJson = null;
        ErrorMessage = null;
    }
}
