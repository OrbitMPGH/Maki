using System.Globalization;
using System.Text.Json;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

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
/// <param name="Priority">The claim class: <c>high</c>, <c>normal</c> or <c>low</c>.</param>
/// <param name="RootFolderId">The series' root folder, which a disk hold is keyed by.</param>
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
    DateTime? CompletedAt,
    string Origin,
    UpgradeQueueInfoDto? Upgrade,
    string? SeriesCoverUrl,
    string Priority = "normal",
    int RootFolderId = 0)
{
    /// <param name="upgradeHistory">The applied upgrade's history state, when the caller looked it up.</param>
    public static QueueItemDto FromEntity(DownloadQueueItem item, Chapter? chapter, Series series, string sourceName,
        UpgradeHistoryState? upgradeHistory = null)
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
            item.CompletedAt,
            item.Origin.ToString().ToLowerInvariant(),
            UpgradeQueueInfoDto.From(item.UpgradeInfoJson, upgradeHistory),
            SeriesDto.CoverUrlFor(series.Id, series.CoverPath, series.LastMetadataRefresh),
            item.Priority.ToString().ToLowerInvariant(),
            series.RootFolderId);
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

/// <summary>One history run: a single row, or one series' burst of the same status, newest first.</summary>
public record QueueHistoryEntryDto(IReadOnlyList<QueueItemDto> Items);

/// <param name="Total">Entries across every page.</param>
/// <param name="ItemTotal">Rows across every page, so the header can say how many chapters the entries hold.</param>
public record QueueHistoryGroupsDto(
    IReadOnlyList<QueueHistoryEntryDto> Entries, int Total, int ItemTotal, int Page, int PageSize);

/// <param name="Active">Items still working: everything not settled, failed or parked for an import decision.</param>
public record QueueSummaryDto(int Active, int AwaitingImport, int Failed);

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

/// <param name="SkipFiles">Downloaded file names to leave out, for Replace and SkipExisting.</param>
/// <param name="ParsingFingerprint">
/// The <c>parsingFingerprint</c> of the plan the user reviewed. When the name parsing settings have
/// changed since, the files would be read differently from what the review showed, so the import is
/// refused. Null skips the check.
/// </param>
public record ImportDecisionDto(
    ImportDecision Mode, IReadOnlyList<string>? SkipFiles = null, string? ParsingFingerprint = null);

public record ImportDecisionResultDto(int Imported, int Linked, int Skipped, int Deleted);

public record QueueClearDto(int Cleared);

/// <summary>One bucket of Failed rows: how many share a failure key and a source.</summary>
/// <param name="ReasonKey">Null for a row whose reason is another program's own text.</param>
public record QueueFailureGroupDto(string? ReasonKey, string SourceName, int Count);

/// <summary>Which Failed rows a bulk retry or remove acts on; both filters null means all of them.</summary>
/// <param name="BlockReleases">Remove only: also stop the volume search grabbing the removed torrent releases again.</param>
public record QueueFailedActionDto(string? Reason = null, string? Source = null, bool BlockReleases = true);

public record QueueBulkResultDto(int Affected);

/// <param name="MovedId">The row the person moved, which takes the claim class of its new neighbour.</param>
public record ReorderQueueDto(IReadOnlyList<int> OrderedIds, int? MovedId = null);

/// <param name="SeriesId">The series whose queued rows to hold.</param>
/// <param name="ResumeAt">When to resume by itself; null pauses until somebody resumes.</param>
public record QueueSeriesPauseRequestDto(int SeriesId, DateTime? ResumeAt = null);

/// <param name="Until">When the pause lifts by itself, in UTC; null means until somebody resumes.</param>
public record QueuePauseEntryDto(DateTime? Until);

public record QueueSourcePauseDto(string SourceName, DateTime? Until);

public record QueueSeriesPauseDto(int SeriesId, string Title, DateTime? Until);

/// <summary>A root folder whose downloads wait for free space. Lifts by itself, so it has no Resume.</summary>
public record QueueDiskHoldDto(int RootFolderId, string Path, long FreeBytes, long MinFreeBytes);

/// <param name="All">Set while every scraper download is paused.</param>
/// <param name="Sources">Sources paused on their own, whether or not everything is.</param>
/// <param name="Series">Series paused on their own, leaving out any the caller cannot see.</param>
/// <param name="DiskHolds">Root folders below their free-space threshold.</param>
public record QueuePauseDto(
    QueuePauseEntryDto? All,
    IReadOnlyList<QueueSourcePauseDto> Sources,
    IReadOnlyList<QueueSeriesPauseDto> Series,
    IReadOnlyList<QueueDiskHoldDto> DiskHolds)
{
    public static QueuePauseDto From(
        Services.DownloadPauseState state,
        IReadOnlyDictionary<int, string>? seriesTitles = null,
        IReadOnlyList<Services.DiskHold>? diskHolds = null) => new(
        state.All is { } all ? new QueuePauseEntryDto(all.Until) : null,
        state.Sources
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new QueueSourcePauseDto(pair.Key, pair.Value.Until))
            .ToList(),
        state.PausedSeries
            .Where(pair => seriesTitles?.ContainsKey(pair.Key) == true)
            .Select(pair => new QueueSeriesPauseDto(pair.Key, seriesTitles![pair.Key], pair.Value.Until))
            .OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList(),
        (diskHolds ?? [])
            .Select(h => new QueueDiskHoldDto(h.RootFolderId, h.Path, h.FreeBytes, h.MinFreeBytes))
            .ToList());

    /// <summary>
    /// The full pause, with the paused series' titles read through <paramref name="db"/>, so a caller
    /// limited to some root folders sees only their own series.
    /// </summary>
    public static async Task<QueuePauseDto> CreateAsync(
        Services.DownloadPauseState state, IReadOnlyList<Services.DiskHold>? diskHolds,
        MakiDbContext db, CancellationToken ct)
    {
        var ids = state.PausedSeries.Keys.ToList();
        var titles = ids.Count == 0
            ? new Dictionary<int, string>()
            : await db.Series.Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Title, ct);
        return From(state, titles, diskHolds);
    }
}

/// <param name="Source">The source to pause, or null for every scraper download.</param>
/// <param name="ResumeAt">When to resume by itself; null pauses until somebody resumes.</param>
public record QueuePauseRequestDto(string? Source = null, DateTime? ResumeAt = null);
