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

    /// <summary>
    /// When a refresh first saw this chapter on this mapping, written once. Null for a mapping's
    /// first snapshot and for back-catalogue chapters a source adds later, because neither is a
    /// release: stamping them would read a whole catalogue as one day's drop.
    /// </summary>
    public DateTime? FirstSeenAt { get; set; }
}
