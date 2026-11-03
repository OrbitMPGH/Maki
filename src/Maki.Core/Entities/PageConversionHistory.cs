namespace Maki.Core.Entities;

/// <summary>
/// One file the library pass converted, and where its original waits in the upgrade trash until it
/// is purged or moved back by an undo.
/// </summary>
public class PageConversionHistory
{
    public int Id { get; set; }
    public int ChapterFileId { get; set; }
    public int SeriesId { get; set; }
    public int? ChapterId { get; set; }

    /// <summary>The start stamp of the pass that converted the file.</summary>
    public string? PassId { get; set; }

    /// <summary>
    /// The original, relative to the series' root folder with forward slashes. Null once purged or
    /// moved back.
    /// </summary>
    public string? TrashPath { get; set; }

    public long? TrashBytes { get; set; }

    /// <summary>What the plan answered, as <c>ChapterFile.CheckedWith</c> records it.</summary>
    public string Action { get; set; } = "";

    public long BytesBefore { get; set; }
    public long BytesAfter { get; set; }
    public string? SourceFormat { get; set; }
    public string? ResultFormat { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UndoneAtUtc { get; set; }
}
