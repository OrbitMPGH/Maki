using Maki.Core.Entities;

namespace Maki.Api.Dtos;

/// <summary>A chapter file's release tier and archive stats.</summary>
/// <param name="Tier">Lowercase <c>QualityTier</c> name: unknown, aggregator, scanlator, official or volume.</param>
/// <param name="Measured">False until the archive has been opened; the stats are null until then.</param>
/// <param name="Score">Format score under the series' upgrade profile. Null when the series has no profile or the file is unmeasured.</param>
/// <param name="CutoffMet">Whether the file already satisfies that profile's cutoff. Null in the same cases as <paramref name="Score"/>.</param>
/// <param name="FileId">The backing <c>ChapterFile</c>'s id, for the per-file actions.</param>
/// <param name="Trusted">"Protect from upgrades".</param>
public record ChapterFileQualityDto(
    string Tier,
    string? Group,
    int? PageCount,
    int? MedianWidth,
    int? MedianHeight,
    string? ImageFormat,
    bool Measured,
    int? Score = null,
    bool? CutoffMet = null,
    int FileId = 0,
    bool Trusted = false)
{
    public static ChapterFileQualityDto From(ChapterFile file, int? score = null, bool? cutoffMet = null) => new(
        file.Tier.ToString().ToLowerInvariant(),
        file.Group,
        file.PageCount,
        file.MedianWidth,
        file.MedianHeight,
        file.ImageFormat,
        file.MeasuredAtUtc is not null,
        score,
        cutoffMet,
        file.Id,
        file.Trusted);
}
