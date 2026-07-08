using Mangarr.Core.Entities;

namespace Mangarr.Api.Dtos;

public record SeriesDto(
    int Id,
    string Title,
    string SortTitle,
    string? OriginalTitle,
    string Status,
    string? Overview,
    int? Year,
    List<string> Genres,
    bool Monitored,
    string MonitorNewItems,
    int RootFolderId,
    string FolderName,
    string? CoverUrl,
    int? TotalChapters,
    int? TotalVolumes,
    string? AuthorStory,
    string? AuthorArt,
    int? MangaBakaId,
    int? AniListId,
    int? MalId,
    DateTime Added,
    int ChapterCount,
    int ChapterFileCount)
{
    public static SeriesDto FromEntity(Series s, int chapterCount = 0, int chapterFileCount = 0) => new(
        s.Id,
        s.Title,
        s.SortTitle,
        s.OriginalTitle,
        s.Status.ToString(),
        s.Overview,
        s.Year,
        s.Genres,
        s.Monitored,
        s.MonitorNewItems.ToString(),
        s.RootFolderId,
        s.FolderName,
        s.CoverPath != null ? $"/api/v1/mediacover/{s.Id}/cover.jpg" : null,
        s.TotalChapters,
        s.TotalVolumes,
        s.AuthorStory,
        s.AuthorArt,
        s.MangaBakaId,
        s.AniListId,
        s.MalId,
        s.Added,
        chapterCount,
        chapterFileCount);
}

public record AddSeriesRequest(
    string MetadataProviderId,
    int RootFolderId,
    bool Monitored = true,
    string MonitorNewItems = "All");
