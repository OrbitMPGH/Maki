namespace Maki.Core.Entities;

public enum SeriesStatus
{
    Unknown = 0,
    Ongoing = 1,
    Completed = 2,
    Hiatus = 3,
    Cancelled = 4
}

/// <summary>
/// MangaBaka's <c>type</c> vocabulary, minus <c>novel</c> (which the provider refuses outright, so
/// no library series can ever carry it). A plain string rather than an enum because it is stored on
/// <see cref="Series.Type"/> exactly as the provider spelled it and is matched against the same
/// values the Discover filters already send.
/// <para>
/// It is what auto-selects a reading profile: manhwa and manhua are read as a vertical strip,
/// manga right-to-left. A series whose metadata has never been refreshed carries null, which
/// matches no profile and falls back to the user's global reader defaults.
/// </para>
/// </summary>
public static class SeriesTypes
{
    public const string Manga = "manga";
    public const string Manhwa = "manhwa";
    public const string Manhua = "manhua";

    /// <summary>Original English language: western-drawn comics in the manga format.</summary>
    public const string Oel = "oel";

    public const string Other = "other";

    public static readonly string[] All = [Manga, Manhwa, Manhua, Oel, Other];

    /// <summary>Lowercases and drops anything outside <see cref="All"/>, so a provider that grows a new value stores null rather than a type nothing understands.</summary>
    public static string? Normalize(string? value)
    {
        var lowered = value?.Trim().ToLowerInvariant();
        return lowered is not null && All.Contains(lowered) ? lowered : null;
    }
}

public enum NewChapterMonitorMode
{
    All = 0,
    None = 1,

    /// <summary>Monitor whole-numbered chapters and one-shots; skip specials (decimal chapters).</summary>
    MainOnly = 2,
    Smart = 3
}

public enum IncognitoMode
{
    Off = 0,

    /// <summary>Excluded from scrobbling only. Still counted in Rewind and read history.</summary>
    ScrobbleOnly = 1,

    /// <summary>Excluded from scrobbling, Rewind stats, and reading history entirely.</summary>
    Full = 2
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
