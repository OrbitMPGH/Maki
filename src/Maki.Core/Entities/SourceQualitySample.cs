namespace Maki.Core.Entities;

/// <summary>
/// One measured copy of a chapter from one source mapping. Kept after the file itself is upgraded
/// away, so a source's track record outlives the files that proved it. One row per mapping and
/// chapter; a newer measurement of the same copy replaces the older one.
/// </summary>
public class SourceQualitySample
{
    public int Id { get; set; }
    public int SourceMappingId { get; set; }
    public SourceMapping? SourceMapping { get; set; }
    public int SeriesId { get; set; }
    public int ChapterId { get; set; }
    public SourceQualityOrigin Origin { get; set; }
    public int PageCount { get; set; }
    public int MedianWidth { get; set; }
    public int MedianHeight { get; set; }
    public long SizeBytes { get; set; }
    public string? ImageFormat { get; set; }
    public DateTime MeasuredAtUtc { get; set; }
}

public enum SourceQualityOrigin
{
    /// <summary>A library file already on disk, measured by the backfill.</summary>
    Library = 0,

    /// <summary>A chapter this source was downloaded from, as a new file or an upgrade.</summary>
    Download = 1,

    /// <summary>A few pages the upgrade scan sampled.</summary>
    Probe = 2
}
