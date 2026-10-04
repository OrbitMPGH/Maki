using System.Globalization;
using Maki.Core.Entities;

namespace Maki.Core.Quality;

/// <summary>
/// Pure scoring and upgrade decisions over a <see cref="QualityCandidate"/>. No database, no disk.
/// </summary>
public static class QualityScorer
{
    /// <summary>
    /// Every required condition must match and, when there are any non-required ones, at least one of
    /// them must too. A format with no conditions never matches.
    /// </summary>
    public static bool Matches(QualityFormat format, QualityCandidate candidate) =>
        Matches(format, candidate, new RegexCache());

    public static bool Matches(QualityFormat format, QualityCandidate candidate, RegexCache regexes)
    {
        if (format.Conditions.Count == 0)
        {
            return false;
        }

        var hasOptional = false;
        var optionalMatched = false;
        foreach (var condition in format.Conditions)
        {
            var hit = ConditionMatches(condition, candidate, regexes);
            if (condition.Required)
            {
                if (!hit)
                {
                    return false;
                }
            }
            else
            {
                hasOptional = true;
                optionalMatched |= hit;
            }
        }

        return !hasOptional || optionalMatched;
    }

    /// <summary>
    /// An unknown attribute, an invalid regex or an unparsable number is false whether or not the
    /// condition is negated: not knowing is never evidence either way.
    /// </summary>
    public static bool ConditionMatches(FormatCondition condition, QualityCandidate candidate) =>
        ConditionMatches(condition, candidate, new RegexCache());

    public static bool ConditionMatches(FormatCondition condition, QualityCandidate candidate, RegexCache regexes) =>
        Evaluate(condition, candidate, regexes) is { } hit && hit != condition.Negate;

    public static QualityScore Score(
        UpgradeProfile profile, IReadOnlyList<QualityFormat> formats, QualityCandidate candidate) =>
        Score(profile, formats, candidate, new RegexCache());

    /// <param name="regexes">Shared across every candidate of one pass, so each pattern is built and can time out only once.</param>
    public static QualityScore Score(
        UpgradeProfile profile, IReadOnlyList<QualityFormat> formats, QualityCandidate candidate, RegexCache regexes)
    {
        var matched = new List<int>();
        var score = 0;
        foreach (var format in formats)
        {
            if (!Matches(format, candidate, regexes))
            {
                continue;
            }

            matched.Add(format.Id);
            foreach (var formatScore in profile.FormatScores)
            {
                if (formatScore.FormatId == format.Id)
                {
                    score += formatScore.Score;
                    break;
                }
            }
        }

        var (resolution, compression) = MeasuredQuality.Points(profile, candidate);
        return new QualityScore(candidate.Tier, score + resolution + compression, matched, resolution, compression);
    }

    /// <summary>
    /// How many tier groups sit below <paramref name="tier"/>'s in <see cref="UpgradeProfile.Tiers"/>,
    /// so grouped tiers rank equal; 0 for a tier the list lacks.
    /// </summary>
    public static int Rank(UpgradeProfile profile, QualityTier tier)
    {
        var index = profile.Tiers.FindIndex(t => t.Tier == tier);
        if (index < 0)
        {
            return 0;
        }

        var rank = 0;
        for (var i = index + 1; i < profile.Tiers.Count; i++)
        {
            if (!profile.Tiers[i].Grouped)
            {
                rank++;
            }
        }

        return rank;
    }

    public static bool Allows(UpgradeProfile profile, QualityTier tier) =>
        profile.Tiers.Any(t => t.Tier == tier && t.Allowed);

    public static bool CutoffMet(UpgradeProfile profile, QualityTier tier, int score) =>
        Rank(profile, tier) >= Rank(profile, profile.Cutoff) &&
        (profile.UpgradeUntilScore == 0 || score >= profile.UpgradeUntilScore);

    /// <param name="trusted">The current file's protect flag.</param>
    public static bool IsUpgrade(
        UpgradeProfile profile, QualityScore current, int? currentPageCount, bool trusted,
        QualityScore candidate, int? candidateWidth, int? candidatePageCount) =>
        IsUpgrade(profile, current, current.Score, currentPageCount, trusted, candidate, candidateWidth,
            candidatePageCount);

    /// <summary>
    /// <see cref="IsUpgrade(UpgradeProfile, QualityScore, int?, bool, QualityScore, int?, int?)"/> for a
    /// candidate scored before anything about it is measured, such as a torrent release. It has no
    /// measured points, so the current file's are left out of the comparison too; the cutoff still reads
    /// the file's full score.
    /// </summary>
    public static bool IsUnmeasuredUpgrade(UpgradeProfile profile, QualityScore current, bool trusted, QualityScore candidate) =>
        IsUpgrade(profile, WithoutMeasured(current), current.Score, null, trusted, WithoutMeasured(candidate),
            candidateWidth: 1, candidatePageCount: null);

    private static QualityScore WithoutMeasured(QualityScore score) => score with
    {
        Score = score.Score - score.ResolutionPoints - score.CompressionPoints,
        ResolutionPoints = 0,
        CompressionPoints = 0
    };

    private static bool IsUpgrade(
        UpgradeProfile profile, QualityScore current, int cutoffScore, int? currentPageCount, bool trusted,
        QualityScore candidate, int? candidateWidth, int? candidatePageCount)
    {
        if (!profile.UpgradesEnabled || trusted)
        {
            return false;
        }

        if (!Allows(profile, candidate.Tier))
        {
            return false;
        }

        if (candidateWidth is null)
        {
            return false;
        }

        if (candidatePageCount < currentPageCount * (1 - profile.PageTolerancePercent / 100.0))
        {
            return false;
        }

        if (current.Tier == QualityTier.Unknown && !profile.AllowReplacingUnknown)
        {
            return false;
        }

        if (CutoffMet(profile, current.Tier, cutoffScore))
        {
            return false;
        }

        var candidateRank = Rank(profile, candidate.Tier);
        var currentRank = Rank(profile, current.Tier);
        if (candidateRank > currentRank)
        {
            return profile.MaxTierScoreDrop is not { } maxDrop || candidate.Score >= current.Score - maxDrop;
        }

        // At least one point even when the profile says 0: an equal score is never an upgrade, or two
        // mirrors of one copy would swap places on every scan.
        return candidateRank == currentRank && candidate.Score >= current.Score + Math.Max(1, profile.MinScoreDelta);
    }

    /// <summary>
    /// The highest score <paramref name="listing"/> could still reach once its unknown attributes are
    /// measured: a positively scored format counts when every condition it can evaluate matches,
    /// treating a condition on an unknown attribute as matched; a zero or negative one only counts
    /// when it matches on what is already known. Unmeasured points count at their maximum.
    /// </summary>
    public static QualityScore OptimisticScore(
        UpgradeProfile profile, IReadOnlyList<QualityFormat> formats, QualityCandidate listing, RegexCache regexes)
    {
        var matched = new List<int>();
        var score = 0;
        foreach (var format in formats)
        {
            var weight = profile.FormatScores.FirstOrDefault(s => s.FormatId == format.Id)?.Score ?? 0;
            var hit = weight > 0 ? MatchesOptimistic(format, listing, regexes) : Matches(format, listing, regexes);
            if (!hit)
            {
                continue;
            }

            matched.Add(format.Id);
            score += weight;
        }

        var (resolution, compression) = MeasuredQuality.OptimisticPoints(profile, listing);
        return new QualityScore(listing.Tier, score + resolution + compression, matched, resolution, compression);
    }

    /// <summary>
    /// <see cref="IsUpgrade"/> on listing data alone: the candidate's width is assumed known, its page
    /// count equal to the current file's, and its score is <see cref="OptimisticScore"/>. False means
    /// no measurement could make this candidate win, so it is not worth probing.
    /// </summary>
    public static bool CouldUpgrade(
        UpgradeProfile profile, IReadOnlyList<QualityFormat> formats, QualityScore current, int? currentPageCount,
        bool trusted, QualityCandidate listing, RegexCache? regexes = null) =>
        IsUpgrade(profile, current, currentPageCount, trusted,
            OptimisticScore(profile, formats, listing, regexes ?? new RegexCache()),
            candidateWidth: listing.MedianWidth ?? 1, candidatePageCount: listing.PageCount ?? currentPageCount);

    private static bool MatchesOptimistic(QualityFormat format, QualityCandidate candidate, RegexCache regexes)
    {
        if (format.Conditions.Count == 0)
        {
            return false;
        }

        var hasOptional = false;
        var optionalMatched = false;
        foreach (var condition in format.Conditions)
        {
            var hit = IsUnknown(condition.Type, candidate) || ConditionMatches(condition, candidate, regexes);
            if (condition.Required)
            {
                if (!hit)
                {
                    return false;
                }
            }
            else
            {
                hasOptional = true;
                optionalMatched |= hit;
            }
        }

        return !hasOptional || optionalMatched;
    }

    private static bool IsUnknown(FormatConditionType type, QualityCandidate c) => type switch
    {
        FormatConditionType.SourceIs => string.IsNullOrWhiteSpace(c.SourceName),
        FormatConditionType.SourceKindIs => c.SourceKind is null,
        FormatConditionType.GroupMatches => string.IsNullOrEmpty(c.Group),
        FormatConditionType.ReleaseNameMatches => string.IsNullOrEmpty(c.ReleaseName),
        FormatConditionType.MinWidth => c.MedianWidth is null,
        FormatConditionType.ImageFormatIs => string.IsNullOrWhiteSpace(c.ImageFormat),
        FormatConditionType.MinBytesPerPage => c.SizeBytes is null || c.PageCount is not > 0,
        FormatConditionType.MinPages => c.PageCount is null,
        FormatConditionType.LanguageIs => string.IsNullOrWhiteSpace(c.Language),
        _ => false
    };

    public static bool IsValidRegex(string pattern) => RegexCache.IsValid(pattern);

    /// <summary>A plain non-negative integer: no sign, no separators, no decimals.</summary>
    public static bool TryParseNumber(string value, out int number) =>
        int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out number);

    public static bool IsRegexType(FormatConditionType type) =>
        type is FormatConditionType.GroupMatches or FormatConditionType.ReleaseNameMatches;

    public static bool IsNumericType(FormatConditionType type) =>
        type is FormatConditionType.MinWidth or FormatConditionType.MinBytesPerPage or FormatConditionType.MinPages;

    private static bool? Evaluate(FormatCondition condition, QualityCandidate c, RegexCache regexes) => condition.Type switch
    {
        FormatConditionType.SourceIs => InList(c.SourceName, condition.Value),
        FormatConditionType.SourceKindIs => InList(c.SourceKind?.ToString(), condition.Value),
        FormatConditionType.GroupMatches => RegexMatch(c.Group, condition.Value, regexes),
        FormatConditionType.ReleaseNameMatches => RegexMatch(c.ReleaseName, condition.Value, regexes),
        FormatConditionType.MinWidth => AtLeast(c.MedianWidth, condition.Value),
        FormatConditionType.ImageFormatIs => InList(c.ImageFormat, condition.Value),
        FormatConditionType.MinBytesPerPage => AtLeast(
            c is { SizeBytes: { } size, PageCount: > 0 and var pages } ? size / pages : null, condition.Value),
        FormatConditionType.MinPages => AtLeast(c.PageCount, condition.Value),
        FormatConditionType.LanguageIs => InList(c.Language, condition.Value),
        _ => null
    };

    private static bool? InList(string? attribute, string list)
    {
        if (string.IsNullOrWhiteSpace(attribute))
        {
            return null;
        }

        foreach (var item in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(item, attribute.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool? RegexMatch(string? attribute, string pattern, RegexCache regexes) =>
        string.IsNullOrEmpty(attribute) ? null : regexes.IsMatch(attribute, pattern);

    private static bool? AtLeast(long? attribute, string value)
    {
        if (attribute is null || !TryParseNumber(value, out var threshold))
        {
            return null;
        }

        return attribute.Value >= threshold;
    }
}
