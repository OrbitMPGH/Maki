namespace Maki.Core.Entities;

/// <summary>
/// One distinct repeated region the credit search judged (a credit page, a banner, a publisher card, a
/// title page), with one verdict for every chapter that carries it (<c>CreditNoticeStore</c>). Only ever
/// accepted on text OCR read, never on the colour rule.
/// </summary>
public class CreditNotice
{
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

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// A page OCR already read for a notice, keyed by its zip entry's CRC32 and length and the share read, so
/// the same bytes in another chapter are never read again.
/// </summary>
public class CreditNoticeRead
{
    public int Id { get; set; }
    public uint Crc { get; set; }
    public long Length { get; set; }

    /// <summary>The share of the page read, in thousandths.</summary>
    public int Share { get; set; }

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
