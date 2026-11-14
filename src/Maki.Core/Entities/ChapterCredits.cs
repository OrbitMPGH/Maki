namespace Maki.Core.Entities;

/// <summary>What judged a chapter's credit candidates.</summary>
public enum CreditJudge
{
    /// <summary>The text read off each candidate, through the keyword rule.</summary>
    Ocr,

    /// <summary>The colour rule alone, because the OCR models were not available.</summary>
    Colour
}

/// <summary>
/// The credit page search's answer for one chapter, found ahead of time so the reader has it before it
/// opens (<c>CreditScanWorker</c>, <c>CreditStore</c>). One row per chapter, because a volume file backs
/// several. The answer belongs to <see cref="EdgeKey"/> (the checksums of the pages an edge strip can
/// take) and to the versions it was found under; a row that matches neither is ignored, never served.
/// </summary>
public class ChapterCredits
{
    public int ChapterId { get; set; }
    public int SeriesId { get; set; }

    /// <summary>The file and its size when the row was written: a cheap "may have changed" test in SQL.</summary>
    public int ChapterFileId { get; set; }
    public long FileSize { get; set; }

    public string EdgeKey { get; set; } = string.Empty;

    /// <summary>The nearby chapters compared against, as their edge keys hashed in order; empty when there were none.</summary>
    public string ReferencesKey { get; set; } = string.Empty;

    /// <summary>The slice's page count when the row was written; 0 on rows written before it was kept.</summary>
    public int PageCount { get; set; }

    /// <summary>
    /// The newest <see cref="CreditBankEntry"/> this chapter was compared against, so an entry added
    /// later is checked against it once (<c>CreditScanWorker</c>), without searching it again.
    /// </summary>
    public int BankSeen { get; set; }

    public int HeadPages { get; set; }
    public double HeadOffset { get; set; }
    public int TailPages { get; set; }

    /// <summary>The share of the last page shown that is hidden, from its bottom (<c>CreditPages.TailOffset</c>).</summary>
    public double TailOffset { get; set; }

    /// <summary><c>CreditDetector.Version</c> when found.</summary>
    public int DetectorVersion { get; set; }

    /// <summary><c>CreditText.Version</c> when the candidates were judged.</summary>
    public int TextVersion { get; set; }

    /// <summary><c>PageTextReader.Version</c> that read <see cref="CandidatesJson"/>'s text.</summary>
    public int OcrVersion { get; set; }

    public CreditJudge Judge { get; set; }

    /// <summary>
    /// Every candidate judged, edge inward, with the text read off it, so a change to the keyword
    /// lists judges again without reading a page. Null when there were none.
    /// </summary>
    public string? CandidatesJson { get; set; }

    /// <summary>
    /// A volume, or a chapter cut from one, that was not searched: its file's first and last pages
    /// read as no scanlator's. A series where any volume's do (<see cref="ProbeHit"/>) is searched whole.
    /// </summary>
    public bool Skipped { get; set; }

    /// <summary>For a volume's chapters: whether the text on the file's first and last pages read as a scanlator's.</summary>
    public bool? ProbeHit { get; set; }

    /// <summary>The search threw; the chapter is not tried again until its file or a version changes.</summary>
    public bool Failed { get; set; }

    public DateTime ScannedAtUtc { get; set; }
}
