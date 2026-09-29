using Maki.Core.Sources;

namespace Maki.Core.Quality;

/// <summary>Everything <see cref="QualityScorer"/> may look at for one chapter file. Null means unknown.</summary>
/// <param name="EstimatedBitsPerPixel">JPG-equivalent bits per pixel from a <see cref="SourceQualityEstimate"/>, for a listing with no size or page count of its own.</param>
public record QualityCandidate(
    QualityTier Tier,
    string? SourceName,
    SourceKind? SourceKind,
    string? Group,
    string? ReleaseName,
    int? PageCount,
    int? MedianWidth,
    string? ImageFormat,
    long? SizeBytes,
    string? Language,
    int? MedianHeight = null,
    double? EstimatedBitsPerPixel = null);

/// <param name="Score">Format scores plus the measured points.</param>
public record QualityScore(
    QualityTier Tier, int Score, IReadOnlyList<int> MatchedFormatIds, int ResolutionPoints = 0, int CompressionPoints = 0);
