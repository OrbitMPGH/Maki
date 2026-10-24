using System.Linq.Expressions;
using Maki.Core.Quality;

namespace Maki.Core.Entities;

public class ChapterFile
{
    /// <summary><see cref="SourceName"/> of a file a relink adopted from disk.</summary>
    public const string RelinkSourceName = "relink";

    /// <summary>
    /// Files that brought something new to the library, for the recently added lists. A relink
    /// adopts a file that was already on disk, and a re-download or health repair restamps
    /// <see cref="DateAdded"/> with <see cref="ReplacedAtUtc"/> set to the same moment.
    /// </summary>
    public static readonly Expression<Func<ChapterFile, bool>> IsNewContent =
        f => f.SourceName != RelinkSourceName && (f.ReplacedAtUtc == null || f.ReplacedAtUtc != f.DateAdded);

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

    /// <summary>The source's own chapter id this file was downloaded from. Null for imports and torrents.</summary>
    public string? SourceChapterId { get; set; }

    /// <summary>"Protect from upgrades". A trusted file is never replaced by the upgrader.</summary>
    public bool Trusted { get; set; }

    /// <summary>
    /// Last successful upgrade, which keeps <see cref="DateAdded"/>. A re-download or health repair
    /// that restamps <see cref="DateAdded"/> sets this to the same value (see <see cref="IsNewContent"/>).
    /// </summary>
    public DateTime? ReplacedAtUtc { get; set; }

    /// <summary>
    /// When page conversion last looked at these bytes, whether it converted them or left them as
    /// they were. Null means never, or new bytes arrived since (<see cref="ResetConversion"/>).
    /// </summary>
    public DateTime? ConversionCheckedAtUtc { get; set; }

    /// <summary>How much smaller conversion made the file. 0 when it was left as it was.</summary>
    public long ConversionSavedBytes { get; set; }

    /// <summary>The page format before conversion. Null when the file was not converted.</summary>
    public string? OriginalImageFormat { get; set; }

    /// <summary>
    /// The size the file had when it was acquired. Upgrade scoring, source quality samples and quality
    /// snapshots read this and <see cref="QualityImageFormat"/>, so converting pages never makes a
    /// copy look like a worse release than the one that was downloaded.
    /// </summary>
    public long QualitySize => Size + ConversionSavedBytes;

    /// <inheritdoc cref="QualitySize"/>
    public string? QualityImageFormat => OriginalImageFormat ?? ImageFormat;

    /// <summary>For new bytes at this row: whatever conversion said about the old ones no longer holds.</summary>
    public void ResetConversion()
    {
        ConversionCheckedAtUtc = null;
        ConversionSavedBytes = 0;
        OriginalImageFormat = null;
    }
}
