namespace Maki.Core.Entities;

public enum SeriesStatus
{
    Unknown = 0,
    Ongoing = 1,
    Completed = 2,
    Hiatus = 3,
    Cancelled = 4
}

public enum NewChapterMonitorMode
{
    All = 0,
    None = 1,

    /// <summary>Monitor whole-numbered chapters and one-shots; skip specials (decimal chapters).</summary>
    MainOnly = 2,
    Smart = 3
}

public enum AcquisitionProtocol
{
    Scraper = 0,
    Torrent = 1,
    Usenet = 2
}

public enum QueueStatus
{
    Queued = 0,
    FetchingPages = 1,
    Downloading = 2,
    Validating = 3,
    Packaging = 4,
    Importing = 5,
    Completed = 6,
    Failed = 7,
    Cancelled = 8,

    /// <summary>
    /// The source rate-limited us. The item stays in the queue and is retried after a
    /// cooldown rather than failing — see <c>DownloadQueueService</c> cooldown gate.
    /// </summary>
    RateLimited = 9
}
