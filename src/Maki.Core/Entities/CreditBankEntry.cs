namespace Maki.Core.Entities;

/// <summary>
/// A credit region the search accepted (repeated in nearby chapters, then read as a scanlator's), kept
/// so a chapter elsewhere carrying the same notice is caught even when nothing near it repeats it: a
/// lone scanlated chapter among volumes, or a group that pastes its card into one chapter of many.
/// One row per distinct notice: a region an entry already matches only counts a hit.
/// </summary>
public class CreditBankEntry
{
    public int Id { get; set; }

    /// <summary>Which edge the region sits at: a tail region is only compared with tails.</summary>
    public bool Tail { get; set; }

    public int SeriesId { get; set; }

    /// <summary>The chapter it was found in; null once that chapter is gone (the entry stays useful).</summary>
    public int? SourceChapterId { get; set; }

    /// <summary>The source chapter's edge strip on that side, as <c>CreditDetector.Edge.Write</c> stores it.</summary>
    public byte[] Strip { get; set; } = [];

    /// <summary>Where the strip's first informative row is, and the rows from it (<c>BankSignature</c>).</summary>
    public int SignatureFirst { get; set; }

    public byte[] Signature { get; set; } = [];

    /// <summary>How many later regions this entry already covered, and how many chapters it caught.</summary>
    public int Hits { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
