using Maki.Core.Quality;

namespace Maki.Core.Entities;

/// <summary>
/// Which chapter file tiers a series accepts, in what order, and when a file is good enough that
/// it stops being a candidate for replacement. Instance-wide: a series pins one through
/// <see cref="Series.UpgradeProfileId"/>, or falls back to the default in settings.
/// </summary>
public class UpgradeProfile
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>What choosing this profile does and costs, in the admin's words. Shown wherever profiles are picked.</summary>
    public string? Description { get; set; }

    /// <summary>Highest priority first. Holds every <see cref="QualityTier"/> exactly once after <see cref="UpgradeProfileDefaults.Normalise"/>.</summary>
    public List<ProfileTier> Tiers { get; set; } = [];

    public QualityTier Cutoff { get; set; } = QualityTier.Aggregator;
    public bool UpgradesEnabled { get; set; }
    public int MinScoreDelta { get; set; } = 1;

    /// <summary>0 means the score plays no part in whether the cutoff is met.</summary>
    public int UpgradeUntilScore { get; set; }

    public List<FormatScore> FormatScores { get; set; } = [];

    /// <summary>Points per doubling of median page width. 0 turns it off. See <see cref="MeasuredQuality"/>.</summary>
    public int ResolutionWeight { get; set; }

    /// <summary>Points per doubling of image data per pixel. 0 turns it off. See <see cref="MeasuredQuality"/>.</summary>
    public int CompressionWeight { get; set; }
    public int PageTolerancePercent { get; set; } = 10;
    public bool AllowReplacingUnknown { get; set; } = true;

    /// <summary>Bumped on every save, so anything cached against the old rules can tell it is stale.</summary>
    public int Version { get; set; } = 1;
}

public record ProfileTier(QualityTier Tier, bool Allowed);

public record FormatScore(int FormatId, int Score);
