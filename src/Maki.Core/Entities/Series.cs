namespace Maki.Core.Entities;

public class Series
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string SortTitle { get; set; } = string.Empty;
    public string? OriginalTitle { get; set; }
    public SeriesStatus Status { get; set; }
    public string? Overview { get; set; }
    public int? Year { get; set; }
    public List<string> Genres { get; set; } = [];
    public List<string> Tags { get; set; } = [];

    // Cross-provider IDs (populated from MangaBaka)
    public int? MangaBakaId { get; set; }
    public int? AniListId { get; set; }
    public int? MalId { get; set; }
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
    public bool HasAnime { get; set; }
    public string? AnimeName { get; set; }
    public string? AnimeStart { get; set; }
    public string? AnimeEnd { get; set; }

    /// <summary>
    /// The user's own rating on a 1–10 scale (null = unrated). Pushed as a score to connected
    /// trackers (MAL 0–10, AniList 0–100, MangaBaka) and used to weight the recommendation
    /// seed vector — highly-rated series pull recommendations harder than unrated ones.
    /// </summary>
    public int? Rating { get; set; }

    /// <summary>
    /// Set when chapter sync detects a cross-source numbering clash (one source
    /// lists x.1/x.2 sub-chapters, another whole chapters). Format:
    /// "subChapterSource|wholeChapterSource". Cleared when the clash goes away.
    /// </summary>
    public string? NumberingClash { get; set; }

    public DateTime Added { get; set; }
    public DateTime? LastMetadataRefresh { get; set; }

    public List<Chapter> Chapters { get; set; } = [];
    public List<SourceMapping> SourceMappings { get; set; } = [];
}
