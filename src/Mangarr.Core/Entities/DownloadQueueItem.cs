namespace Mangarr.Core.Entities;

public class DownloadQueueItem
{
    public int Id { get; set; }
    public int ChapterId { get; set; }
    public Chapter? Chapter { get; set; }

    /// <summary>Null for phase-2 items acquired via indexer releases instead of a scraper.</summary>
    public int? SourceMappingId { get; set; }
    public SourceMapping? SourceMapping { get; set; }

    public AcquisitionProtocol Protocol { get; set; } = AcquisitionProtocol.Scraper;

    /// <summary>Serialized release info for phase-2 torrent/usenet acquisitions.</summary>
    public string? ReleaseInfoJson { get; set; }

    public QueueStatus Status { get; set; } = QueueStatus.Queued;
    public int PagesTotal { get; set; }
    public int PagesDone { get; set; }
    public int RetryCount { get; set; }
    public string? ErrorMessage { get; set; }

    public DateTime QueuedAt { get; set; }
    public DateTime? NextAttempt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
