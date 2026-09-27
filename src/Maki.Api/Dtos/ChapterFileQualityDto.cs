using Maki.Core.Entities;

namespace Maki.Api.Dtos;

/// <summary>A chapter file's release tier and archive stats.</summary>
/// <param name="Tier">Lowercase <c>QualityTier</c> name: unknown, aggregator, scanlator, official or volume.</param>
/// <param name="Measured">False until the archive has been opened; the stats are null until then.</param>
public record ChapterFileQualityDto(
    string Tier,
    string? Group,
    int? PageCount,
    int? MedianWidth,
    int? MedianHeight,
    string? ImageFormat,
    bool Measured)
{
    public static ChapterFileQualityDto From(ChapterFile file) => new(
        file.Tier.ToString().ToLowerInvariant(),
        file.Group,
        file.PageCount,
        file.MedianWidth,
        file.MedianHeight,
        file.ImageFormat,
        file.MeasuredAtUtc is not null);
}
