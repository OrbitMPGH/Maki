using Maki.Core.Quality;

namespace Maki.Core.Entities;

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

    /// <summary>
    /// The original torrent/usenet release name, kept so the UI can still show what release a
    /// file came from after the naming format renames it. Null for scraped and manually
    /// imported files.
    /// </summary>
    public string? ReleaseName { get; set; }

    public QualityTier Tier { get; set; } = QualityTier.Unknown;

    /// <summary>Scanlation group or release group that produced this file, when known.</summary>
    public string? Group { get; set; }

    public int? PageCount { get; set; }
    public int? MedianWidth { get; set; }
    public int? MedianHeight { get; set; }
    public string? ImageFormat { get; set; }

    /// <summary>When this file was last measured. Null means never measured.</summary>
    public DateTime? MeasuredAtUtc { get; set; }
}
