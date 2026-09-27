using Maki.Core.Sources;

namespace Maki.Core.Quality;

/// <summary>Everything <see cref="QualityScorer"/> may look at for one chapter file. Null means unknown.</summary>
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
    string? Language);

public record QualityScore(QualityTier Tier, int Score, IReadOnlyList<int> MatchedFormatIds);
