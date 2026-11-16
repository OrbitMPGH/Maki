namespace Maki.Core.Entities;

/// <summary>
/// One distinct repeated region the credit search judged (a credit page, a banner, a publisher card, a
/// title page), with one verdict for every chapter that carries it (<c>CreditNoticeStore</c>). Only ever
/// accepted on text OCR read, never on the colour rule.
/// </summary>
public class CreditNotice
{
    /// <summary>Longest group name stored, confirmed or suggested.</summary>
    public const int MaxNameLength = 100;

    public int Id { get; set; }

    /// <summary>A whole page, or the top (head) or bottom (tail) <see cref="Share"/> of one.</summary>
    public bool Whole { get; set; }

    public double Share { get; set; }

    /// <summary>The region's grey rows at <c>CreditDetector.Width</c> columns, top to bottom, and how many.</summary>
    public int Height { get; set; }

    public byte[] Rows { get; set; } = [];

    /// <summary>The region's 16 by 16 mean-grey thumbnail as floats (<c>CreditNotices.Fingerprint</c>).</summary>
    public byte[] Fingerprint { get; set; } = [];

    public bool Accepted { get; set; }

    /// <summary>How many independent OCR reads judged it. A page with the same bytes as one read before is not one.</summary>
    public int Reads { get; set; }

    /// <summary>The accepting read's text, else the last read's.</summary>
    public string? Text { get; set; }

    /// <summary><c>PageTextReader.Version</c> that read it; a notice from another is never used.</summary>
    public int OcrVersion { get; set; }

    /// <summary><c>CreditText.Version</c> the verdict was judged under; below it the verdict is judged again from <see cref="CreditNoticeRead"/>s.</summary>
    public int TextVersion { get; set; }

    /// <summary>What the notice is: a release group's credit or ad, an aggregator site's ad, or not known yet.</summary>
    public CreditNoticeKind Kind { get; set; }

    /// <summary>The release group the notice belongs to, set by propagation or by the user (<see cref="GroupSource"/>).</summary>
    public string? GroupName { get; set; }

    public CreditGroupSource GroupSource { get; set; }

    /// <summary>
    /// A group name read off the text (<c>CreditGroupName</c>). Only ever shown to the user to confirm; never
    /// applied to a chapter by itself.
    /// </summary>
    public string? SuggestedName { get; set; }

    /// <summary>A Discord invite code found in the text: a stable id shared by the notices of one group.</summary>
    public string? Invite { get; set; }

    /// <summary><c>CreditGroupName.Version</c> that filled <see cref="SuggestedName"/> and <see cref="Invite"/>.</summary>
    public int NameVersion { get; set; }

    /// <summary>The user's decision about the notice, which outranks every read.</summary>
    public CreditNoticeOverride Override { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public enum CreditNoticeKind
{
    Unknown = 0,

    /// <summary>A release group's credit page or ad.</summary>
    Group = 1,

    /// <summary>An aggregator or reader site's ad. Never names a chapter's group.</summary>
    Site = 2,
}

public enum CreditGroupSource
{
    None = 0,

    /// <summary>Taken from the group most of the notice's chapters already carry. Replaced as that changes.</summary>
    Propagated = 1,

    /// <summary>Set by the user. Never overwritten automatically.</summary>
    Confirmed = 2,
}

public enum CreditNoticeOverride
{
    None = 0,

    /// <summary>Always hidden, whatever OCR read.</summary>
    Hide = 1,

    /// <summary>Always kept, whatever OCR read.</summary>
    Keep = 2,
}

/// <summary>
/// A page OCR already read for a notice, keyed by its zip entry's CRC32 and length and the share read (from
/// the top, or from the bottom for a tail's partial region), so the same bytes in another chapter are never
/// read again.
/// </summary>
public class CreditNoticeRead
{
    public int Id { get; set; }
    public uint Crc { get; set; }
    public long Length { get; set; }

    /// <summary>The share of the page read, in thousandths.</summary>
    public int Share { get; set; }

    /// <summary>Whether <see cref="Share"/> was read from the bottom of the page (a tail's partial region) rather than the top.</summary>
    public bool Bottom { get; set; }

    public string Text { get; set; } = string.Empty;

    /// <summary>The colour rule's verdict on the candidate the text was read for, which <c>CreditText</c> weighs.</summary>
    public bool Colour { get; set; }

    public int NoticeId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>
/// Which notice a judged candidate of a chapter was, and the verdict applied to it. Rewritten every time
/// the chapter is searched; read from the notice's side to find the chapters that carry it.
/// </summary>
public class ChapterCreditNotice
{
    public int Id { get; set; }
    public int ChapterId { get; set; }
    public int SeriesId { get; set; }
    public bool Tail { get; set; }

    /// <summary>Pages from the edge: 0 is the first page of a head, the last of a tail.</summary>
    public int Page { get; set; }

    /// <summary>The share of the page the candidate covers, in thousandths.</summary>
    public int Share { get; set; }

    public int NoticeId { get; set; }
    public bool Accepted { get; set; }
}
