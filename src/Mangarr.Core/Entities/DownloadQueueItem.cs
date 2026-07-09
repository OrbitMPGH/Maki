namespace Mangarr.Core.Entities;

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
}
