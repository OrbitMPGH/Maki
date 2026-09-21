using System.Globalization;
using System.Text.Json;
using Maki.Core.Entities;

namespace Maki.Api.Dtos;

/// <summary>
/// One row of the download queue.
/// <para>
/// Nothing here is a sentence in a language. The chapter label and the failure reason both reach the
/// client as the pieces they are made of, and the client words them: a queue update is broadcast
/// over SignalR to every connected client at once, and those clients do not share a language, so
/// there is no locale to render either of them in on the way out.
/// </para>
/// </summary>
/// <param name="ChapterTitle">
/// The chapter's own title, for a one-shot or an unnumbered chapter. Null when the chapter is
/// numbered, in which case <paramref name="ChapterVolume"/> and <paramref name="ChapterNumber"/>
/// are what name it.
/// </param>
/// <param name="ChapterNumber">
/// Null for a torrent grab, which is series-level and named by <paramref name="ReleaseTitle"/>.
/// </param>
/// <param name="ErrorKey">
/// The catalogue key for why the item stopped, or null when the reason is not Maki's own words.
/// </param>
/// <param name="ErrorParams">Values for that message's placeholders.</param>
/// <param name="ErrorMessage">
/// Text from outside Maki, or English written before the queue was keyed. Shown as-is when
/// <paramref name="ErrorKey"/> is null.
/// </param>
public record QueueItemDto(
    int Id,
    int ChapterId,
    int SeriesId,
    string SeriesTitle,
    string? ReleaseTitle,
    string? ChapterTitle,
    int? ChapterVolume,
    string? ChapterNumber,
    string SourceName,
    string Status,
    int PagesTotal,
    int PagesDone,
    int RetryCount,
    DateTime? NextAttempt,
    string? ErrorKey,
    IReadOnlyDictionary<string, JsonElement>? ErrorParams,
    string? ErrorMessage,
    DateTime QueuedAt,
    DateTime? CompletedAt)
{
    public static QueueItemDto FromEntity(DownloadQueueItem item, Chapter? chapter, Series series, string sourceName)
    {
        // Invariant, as everywhere this app formats a chapter number: it is an identifier being put
        // on the wire, not a number being shown to anyone. A culture that writes "12,5" here would
        // not survive the round trip.
        var number = chapter?.Number is { } n && !chapter.IsOneShot
            ? n.ToString("0.###", CultureInfo.InvariantCulture)
            : null;

        return new QueueItemDto(
            item.Id,
            item.ChapterId ?? 0,
            series.Id,
            series.Title,
            chapter is null ? item.Title : null,
            number is null ? chapter?.Title : null,
            number is null ? null : chapter?.Volume,
            number,
            sourceName,
            item.Status.ToString(),
            item.PagesTotal,
            item.PagesDone,
            item.RetryCount,
            item.NextAttempt,
            item.ErrorKey,
            ParseParams(item.ErrorParamsJson),
            item.ErrorMessage,
            item.QueuedAt,
            item.CompletedAt);
    }

    /// <summary>
    /// Never throws. A row whose params JSON is malformed still has a key worth showing, and a
    /// queue that will not load because one row's metadata is bad is worse than a missing word.
    /// </summary>
    private static IReadOnlyDictionary<string, JsonElement>? ParseParams(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
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
