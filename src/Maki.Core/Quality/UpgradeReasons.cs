using Maki.Core.Entities;

namespace Maki.Core.Quality;

/// <summary>
/// Why a candidate was not taken, stored raw on <c>UpgradeAttempt.Reason</c> and worded by the client.
/// Persisted, so never rename one.
/// </summary>
public static class UpgradeReasons
{
    public const string TierNotAllowed = "tier_not_allowed";
    public const string ScoreNotHigher = "score_not_higher";
    public const string FewerPages = "fewer_pages";
    public const string Unmeasurable = "unmeasurable";
    public const string QuietPeriod = "quiet_period";
    public const string ProbeFailed = "probe_failed";
    public const string SourceCooldown = "source_cooldown";
    public const string Enqueued = "enqueued";
    public const string UpgradeRejected = "upgrade_rejected";
    public const string RevertedByUser = "reverted_by_user";

    /// <summary>
    /// Not probed: the source's recent measurements (<see cref="SourceQualityEstimate"/>) say it would
    /// not win. Expires after <see cref="EstimateMemoLifetime"/>, since a source can improve.
    /// </summary>
    public const string EstimateNotHigher = "estimate_not_higher";

    public static readonly TimeSpan EstimateMemoLifetime = TimeSpan.FromDays(30);

    /// <summary>A chapter no enabled source lists except the one its file already came from.</summary>
    public const string NoOtherSource = "no_other_source";

    /// <summary>The file on disk is not an archive (a PDF), so a downloaded copy cannot take its place.</summary>
    public const string UnsupportedFile = "unsupported_file";

    /// <summary>
    /// The first <see cref="QualityScorer.IsUpgrade"/> guard a losing candidate trips, in the same
    /// order. Anything that is not about the candidate itself reads as <see cref="ScoreNotHigher"/>.
    /// </summary>
    public static string Explain(UpgradeProfile profile, int? currentPageCount, QualityScore candidate,
        int? candidateWidth, int? candidatePageCount)
    {
        if (!QualityScorer.Allows(profile, candidate.Tier)) return TierNotAllowed;
        if (candidateWidth is null) return Unmeasurable;
        if (candidatePageCount < currentPageCount * (1 - profile.PageTolerancePercent / 100.0)) return FewerPages;
        return ScoreNotHigher;
    }
}
