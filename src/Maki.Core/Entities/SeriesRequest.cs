using Maki.Core.Security;

namespace Maki.Core.Entities;

public enum SeriesRequestKind
{
    /// <summary>A title that isn't in the library yet. Carries a metadata provider id to add from.</summary>
    NewSeries = 0,

    /// <summary>Chapters of a series the library already holds. Carries <see cref="SeriesRequest.SeriesId"/>.</summary>
    Chapters = 1,
}

public enum SeriesRequestStatus
{
    Pending = 0,

    /// <summary>An admin actioned it — the series was added and/or the chapters were queued.</summary>
    Approved = 1,

    Rejected = 2,
}

/// <summary>
/// What a user without <see cref="MakiPermission.AddSeries"/> (or
/// <see cref="MakiPermission.DownloadChapters"/>) asks an admin for, instead of being handed a
/// button that answers 403.
/// <para>
/// The display fields (<see cref="Title"/>, <see cref="CoverUrl"/>, <see cref="Year"/>) are a
/// snapshot taken at request time rather than looked up when the Requests page renders: the
/// alternative is one MangaBaka round trip per pending row, and a request whose provider id has
/// since been merged away would render as a blank card with no way to tell what was asked for.
/// </para>
/// <para>
/// <see cref="ChapterStart"/>/<see cref="ChapterEnd"/> are both null for "everything" — the common
/// case, and the only thing a <see cref="SeriesRequestKind.NewSeries"/> request can mean before the
/// chapter list exists.
/// </para>
/// </summary>
public class SeriesRequest : IUserOwned
{
    public int Id { get; set; }

    /// <summary>Who asked. The query filter keys on this; the Requests page ignores it for admins.</summary>
    public int UserId { get; set; }

    public SeriesRequestKind Kind { get; set; }
    public SeriesRequestStatus Status { get; set; }

    /// <summary>MangaBaka id for a <see cref="SeriesRequestKind.NewSeries"/> request; null otherwise.</summary>
    public string? MetadataProviderId { get; set; }

    /// <summary>
    /// The library series. Set from the start for a chapter request, and stamped on a new-series
    /// request once it is approved and the series exists. Nulled rather than cascaded when the
    /// series is deleted, same as <see cref="StatsEvent.SeriesId"/> — the request is a record of
    /// what somebody asked for, and deleting the series doesn't unask it.
    /// </summary>
    public int? SeriesId { get; set; }

    public string Title { get; set; } = string.Empty;
    public string? CoverUrl { get; set; }
    public int? Year { get; set; }

    /// <summary>Inclusive lower bound of the requested range; null with <see cref="ChapterEnd"/> means all.</summary>
    public decimal? ChapterStart { get; set; }

    /// <summary>Inclusive upper bound; null with a set <see cref="ChapterStart"/> means "from there on".</summary>
    public decimal? ChapterEnd { get; set; }

    /// <summary>Free text from the requester.</summary>
    public string? Note { get; set; }

    public DateTime Created { get; set; }

    /// <summary>
    /// When an admin last narrowed the range, and to whom. Doubles as the "has been edited" flag:
    /// <see cref="OriginalChapterStart"/> and <see cref="OriginalChapterEnd"/> only mean anything
    /// when this is set, since null is a legitimate bound in its own right and a nullable snapshot
    /// column alone could not tell "asked for everything" from "never edited".
    /// </summary>
    public DateTime? EditedAt { get; set; }
    public int? EditedByUserId { get; set; }

    /// <summary>
    /// The bounds as the requester originally asked for them, captured on the first edit only.
    /// Kept so the row can say "asked for everything, trimmed to 1–10" rather than quietly
    /// presenting the admin's range as what somebody wanted.
    /// </summary>
    public decimal? OriginalChapterStart { get; set; }
    public decimal? OriginalChapterEnd { get; set; }

    public DateTime? ResolvedAt { get; set; }
    public int? ResolvedByUserId { get; set; }

    /// <summary>Why it was rejected, or what the admin did. Shown back to the requester.</summary>
    public string? ResolutionNote { get; set; }

    /// <summary>How many chapters approving it actually queued. Null until resolved.</summary>
    public int? QueuedCount { get; set; }
}
