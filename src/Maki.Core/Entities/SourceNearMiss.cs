namespace Maki.Core.Entities;

/// <summary>
/// The best result a source search returned for a series that still fell short of an automatic
/// match, kept so the Sources card can ask "Is this it?" without searching again. At most one
/// undismissed row exists per series and source; a dismissed row is kept so the same candidate is
/// not offered again.
/// </summary>
public class SourceNearMiss
{
    public int Id { get; set; }
    public int SeriesId { get; set; }

    public string SourceName { get; set; } = string.Empty;
    public string SourceSeriesId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? CoverUrl { get; set; }
    public string? Author { get; set; }
    public string? Language { get; set; }

    /// <summary>A short excerpt of the source's blurb, enough to tell two similarly named works apart.</summary>
    public string? Description { get; set; }

    /// <summary>The title similarity that put it here, between the near-miss floor and the match threshold.</summary>
    public double Score { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DismissedAt { get; set; }
}
