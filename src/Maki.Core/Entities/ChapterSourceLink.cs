namespace Maki.Core.Entities;

/// <summary>
/// One mapping's last successful view of a chapter. The source-specific metadata lets cleanup
/// rebuild a retained chapter after the mapping that originally populated it is removed.
/// </summary>
public class ChapterSourceLink
{
    public int ChapterId { get; set; }
    public Chapter? Chapter { get; set; }

    public int SourceMappingId { get; set; }
    public SourceMapping? SourceMapping { get; set; }

    public string SourceChapterId { get; set; } = string.Empty;
    public string? NumberRaw { get; set; }
    public int? Volume { get; set; }
    public string? Title { get; set; }
    public DateTime? ReleaseDate { get; set; }
    public string? Group { get; set; }

    /// <summary>Pages the source's listing reports for this chapter, when its listing says.</summary>
    public int? PageCount { get; set; }

    /// <summary>
    /// This upload's page hashes, sixteen hex digits per page (<see cref="Maki.Core.Sources.ChapterPageMatch"/>), for
    /// checking a chapter fold against the pages. Only kept while the source lists the same upload.
    /// </summary>
    public string? PageHashes { get; set; }

    /// <summary>
    /// Set when this link is a folded chapter (<see cref="ChapterFold"/>): the uploads that make it up,
    /// in order, as <see cref="ChapterFoldPart"/> JSON. <see cref="SourceChapterId"/> is the first of
    /// them. A download fetches every part and joins their pages.
    /// </summary>
    public string? Parts { get; set; }

    /// <summary>The chapter's page on the source, when the source lists one. Written into ComicInfo's Notes.</summary>
    public string? Url { get; set; }

    /// <summary>
    /// When a refresh first saw this chapter on this mapping, written once. Null for a mapping's
    /// first snapshot and for back-catalogue chapters a source adds later, because neither is a
    /// release: stamping them would read a whole catalogue as one day's drop.
    /// </summary>
    public DateTime? FirstSeenAt { get; set; }
}
