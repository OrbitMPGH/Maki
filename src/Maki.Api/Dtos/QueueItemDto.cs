using System.Globalization;
using Maki.Core.Entities;

namespace Maki.Api.Dtos;

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
    DateTime? NextAttempt,
    string? ErrorMessage,
    DateTime QueuedAt,
    DateTime? CompletedAt)
{
    public static QueueItemDto FromEntity(DownloadQueueItem item, Chapter? chapter, Series series, string sourceName)
    {
        // Scraper items are per-chapter; torrent grabs are series-level and show the release title.
        var label = chapter is null
            ? item.Title ?? "Release"
            : chapter.IsOneShot || chapter.Number is null
                ? chapter.Title ?? "One-shot"
                : (chapter.Volume is int v ? $"Vol.{v} " : string.Empty) +
                  $"Ch.{chapter.Number.Value.ToString("0.###", CultureInfo.InvariantCulture)}";

        return new QueueItemDto(
            item.Id,
            item.ChapterId ?? 0,
            series.Id,
            series.Title,
            label,
            sourceName,
            item.Status.ToString(),
            item.PagesTotal,
            item.PagesDone,
            item.RetryCount,
            item.NextAttempt,
            item.ErrorMessage,
            item.QueuedAt,
            item.CompletedAt);
    }
}

public record QueueHistoryDto(IReadOnlyList<QueueItemDto> Items, int Total, int Page, int PageSize);

/// <summary>What to do with a download parked as <see cref="QueueStatus.AwaitingImport"/>.</summary>
public enum ImportDecision
{
    /// <summary>Import every file and delete the ones it supersedes.</summary>
    Replace,

    /// <summary>Import only chapters the library has no file for; leave the rest where they are.</summary>
    SkipExisting,

    /// <summary>Import nothing. The torrent keeps seeding; the library is untouched.</summary>
    Reject
}

public record ImportDecisionDto(ImportDecision Mode);

public record ImportDecisionResultDto(int Imported, int Linked, int Skipped, int Deleted);

public record QueueClearDto(int Cleared);

public record ReorderQueueDto(IReadOnlyList<int> OrderedIds);
