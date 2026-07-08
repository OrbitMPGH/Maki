using Mangarr.Core.Entities;

namespace Mangarr.Api.Dtos;

public record QueueItemDto(
    int Id,
    int ChapterId,
    int SeriesId,
    string SeriesTitle,
    string ChapterLabel,
    string SourceName,
    string Status,
    int PagesTotal,
    int PagesDone,
    int RetryCount,
    string? ErrorMessage,
    DateTime QueuedAt,
    DateTime? CompletedAt)
{
    public static QueueItemDto FromEntity(DownloadQueueItem item, Chapter chapter, Series series, string sourceName)
    {
        var label = chapter.IsOneShot || chapter.Number is null
            ? chapter.Title ?? "One-shot"
            : (chapter.Volume is int v ? $"Vol.{v} " : string.Empty) + $"Ch.{chapter.Number:0.###}";

        return new QueueItemDto(
            item.Id,
            item.ChapterId,
            series.Id,
            series.Title,
            label,
            sourceName,
            item.Status.ToString(),
            item.PagesTotal,
            item.PagesDone,
            item.RetryCount,
            item.ErrorMessage,
            item.QueuedAt,
            item.CompletedAt);
    }
}
