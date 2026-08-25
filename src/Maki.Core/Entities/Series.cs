namespace Maki.Core.Entities;

public class Series
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string SortTitle { get; set; } = string.Empty;
    public string? OriginalTitle { get; set; }
    /// <summary>Other primary titles from the provider besides <see cref="Title"/> and <see cref="OriginalTitle"/>.</summary>
    public List<string> AltTitles { get; set; } = [];
    public SeriesStatus Status { get; set; }

    /// <summary>
    /// One of <see cref="SeriesTypes"/>, as the metadata provider spelled it, or null when the
    /// series has never been refreshed since the column was added (it is filled by the daily
    /// metadata job and by the Library's bulk "Metadata" action, not by the upgrade itself).
    /// <para>
    /// Read by reading-profile resolution: a manhwa opens as a continuous left-to-right strip
    /// without anyone configuring it. Null matches no profile, so an un-refreshed series falls
    /// back to the reader's global defaults, which is the pre-profiles behaviour.
    /// </para>
    /// </summary>
    public string? Type { get; set; }

    public string? Overview { get; set; }
    public int? Year { get; set; }
    public List<string> Genres { get; set; } = [];
    public List<string> Tags { get; set; } = [];

    /// <summary>
    /// One of the "safe"/"suggestive"/"erotica"/"pornographic" vocabulary, or null when the series
    /// predates the column and has not been refreshed since (it is filled by metadata refresh, not
    /// backfilled on upgrade — same pattern as <see cref="Type"/>). Powers the Library/Discover
    /// content-rating filter.
    /// </summary>
    public string? ContentRating { get; set; }

    // Cross-provider IDs (populated from MangaBaka)
    public int? MangaBakaId { get; set; }
    public int? AniListId { get; set; }
    public int? MalId { get; set; }
    public int? KitsuId { get; set; }
    public string? MangaUpdatesId { get; set; }
    public string? MangaDexUuid { get; set; }

    /// <summary>
    /// Which chapters are monitored, now and as new ones appear. There is deliberately no
    /// series-level monitored flag: it was write-once at Add and nothing updated it, so setting
    /// "Monitor: none" left the library card still claiming the series was monitored. Monitoring
    /// state is whatever this says.
    /// </summary>
    public NewChapterMonitorMode MonitorNewItems { get; set; } = NewChapterMonitorMode.All;

    public int RootFolderId { get; set; }
    public RootFolder? RootFolder { get; set; }
    public string FolderName { get; set; } = string.Empty;

    public string? CoverPath { get; set; }
    public int? TotalChapters { get; set; }
    public int? TotalVolumes { get; set; }
    public string? AuthorStory { get; set; }
    public string? AuthorArt { get; set; }
    public string? Publisher { get; set; }
    public bool HasAnime { get; set; }
    public string? AnimeName { get; set; }
    public string? AnimeStart { get; set; }
    public string? AnimeEnd { get; set; }

    /// <summary>
    /// Set when chapter sync detects a cross-source numbering clash (one source
    /// lists x.1/x.2 sub-chapters, another whole chapters). Format:
    /// "subChapterSource|wholeChapterSource". Cleared when the clash goes away.
    /// </summary>
    public string? NumberingClash { get; set; }

    // Rating and the per-series reader override used to live here. They are per-reader, not per
    // series, so they moved to UserSeriesState — a shared column meant one person's score was
    // pushed to another person's tracker profile.

    /// <summary>
    /// Auto source matching is queued or running for this series. Adding a series no longer waits
    /// for it — searching every source takes tens of seconds — so the row lands first and the flag
    /// is what the series page renders a spinner from.
    /// <para>
    /// A column rather than an in-memory set because the work outlives no restart: a process that
    /// dies mid-match would otherwise leave a series with no sources and nothing left anywhere
    /// saying it was ever supposed to get any. <c>SourceMatchWorker</c> re-queues everything still
    /// flagged at startup, so the failure mode is a delay, not a series stuck sourceless.
    /// </para>
    /// </summary>
    public bool SourceMatchPending { get; set; }

    /// <summary>
    /// <see cref="IncognitoMode.ScrobbleOnly"/> excludes this series from scrobbling. <see
    /// cref="IncognitoMode.Full"/> also excludes it from Rewind/reading-history <c>StatsEvent</c>s.
    /// Gated at write time (<c>StatsEventService</c>, <c>ReadingProgressService</c>,
    /// <c>ScrobbleService</c>), not at read time, so nothing needs to filter it back out later.
    /// </summary>
    public IncognitoMode Incognito { get; set; } = IncognitoMode.Off;

    public DateTime Added { get; set; }
    public DateTime? LastMetadataRefresh { get; set; }

    public List<Chapter> Chapters { get; set; } = [];
    public List<SourceMapping> SourceMappings { get; set; } = [];

    /// <summary>
    /// User-assigned <see cref="Tag"/>s. Separate from <see cref="Tags"/>, which is metadata the
    /// provider owns and overwrites on refresh.
    /// </summary>
    public List<Tag> UserTags { get; set; } = [];
}
