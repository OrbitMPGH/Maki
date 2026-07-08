namespace Mangarr.Core.Entities;

public class ChapterFile
{
    public int Id { get; set; }
    public int SeriesId { get; set; }

    /// <summary>Path relative to the series' root folder.</summary>
    public string RelativePath { get; set; } = string.Empty;

    public long Size { get; set; }
    public string SourceName { get; set; } = string.Empty;
    public DateTime DateAdded { get; set; }

    /// <summary>Torrent/usenet release hash for phase-2 dedupe.</summary>
    public string? ReleaseHash { get; set; }
}
