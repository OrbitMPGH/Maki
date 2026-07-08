namespace Mangarr.Core.Entities;

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

    public bool Monitored { get; set; } = true;
    public NewChapterMonitorMode MonitorNewItems { get; set; } = NewChapterMonitorMode.All;

    public int RootFolderId { get; set; }
    public RootFolder? RootFolder { get; set; }
    public string FolderName { get; set; } = string.Empty;

    public string? CoverPath { get; set; }
    public int? TotalChapters { get; set; }
    public int? TotalVolumes { get; set; }
    public string? AuthorStory { get; set; }
    public string? AuthorArt { get; set; }

    public DateTime Added { get; set; }
    public DateTime? LastMetadataRefresh { get; set; }

    public List<Chapter> Chapters { get; set; } = [];
    public List<SourceMapping> SourceMappings { get; set; } = [];
}
