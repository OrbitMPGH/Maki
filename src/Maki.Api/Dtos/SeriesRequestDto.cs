using Maki.Core.Entities;

namespace Maki.Api.Dtos;

/// <summary>
/// A request as the Requests page renders it. <see cref="RequestedBy"/> is the display name resolved
/// server-side — the page is admin-facing and a bare user id tells nobody anything.
/// </summary>
public record SeriesRequestDto(
    int Id,
    string Kind,
    string Status,
    int UserId,
    string RequestedBy,
    string? MetadataProviderId,
    int? SeriesId,
    string Title,
    string? CoverUrl,
    int? Year,
    decimal? ChapterStart,
    decimal? ChapterEnd,
    string? Note,
    DateTime Created,
    DateTime? ResolvedAt,
    string? ResolvedBy,
    string? ResolutionNote,
    int? QueuedCount,
    DateTime? EditedAt,
    string? EditedBy,
    decimal? OriginalChapterStart,
    decimal? OriginalChapterEnd)
{
    public static SeriesRequestDto FromEntity(
        SeriesRequest r, string requestedBy, string? resolvedBy, string? editedBy) =>
        new(r.Id,
            r.Kind.ToString(),
            r.Status.ToString(),
            r.UserId,
            requestedBy,
            r.MetadataProviderId,
            r.SeriesId,
            r.Title,
            r.CoverUrl,
            r.Year,
            r.ChapterStart,
            r.ChapterEnd,
            r.Note,
            r.Created,
            r.ResolvedAt,
            resolvedBy,
            r.ResolutionNote,
            r.QueuedCount,
            r.EditedAt,
            editedBy,
            // Only meaningful once edited — null is a legitimate bound, so the flag is EditedAt.
            r.EditedAt is null ? null : r.OriginalChapterStart,
            r.EditedAt is null ? null : r.OriginalChapterEnd);
}

/// <summary>
/// What the client asks for. The title/cover/year snapshot is deliberately *not* taken from here —
/// the server resolves it from the metadata provider or the series row, so an admin approving a
/// request is looking at what was actually asked for rather than at text a client supplied.
/// </summary>
/// <param name="Kind">"NewSeries" or "Chapters".</param>
public record CreateSeriesRequestBody(
    string Kind,
    string? MetadataProviderId = null,
    int? SeriesId = null,
    decimal? ChapterStart = null,
    decimal? ChapterEnd = null,
    string? Note = null);

/// <summary>
/// <c>RootFolderId</c> is required for a <see cref="SeriesRequestKind.NewSeries"/> approval — where
/// a series lives is an admin's call, not the requester's, since a requester may not even be able to
/// see every root folder.
/// </summary>
public record ApproveSeriesRequestBody(
    int? RootFolderId = null,
    string MonitorNewItems = "All",
    string? Note = null);

public record RejectSeriesRequestBody(string? Note = null);

/// <summary>
/// An admin narrowing (or widening) what a pending request asks for — "1 to latest" trimmed to the
/// first ten. Both bounds are sent every time, so clearing one back to "unbounded" is expressible;
/// a partial update could not say "remove the upper bound" without a tri-state.
/// </summary>
public record EditSeriesRequestBody(decimal? ChapterStart = null, decimal? ChapterEnd = null);
