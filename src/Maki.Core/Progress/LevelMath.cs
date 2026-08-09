namespace Maki.Core.Progress;

/// <summary>
/// Turns a reader's totals into experience and a level. Pure arithmetic over numbers the caller has
/// already aggregated, so it holds no state and can be retuned in a release without a migration —
/// nothing derived here is ever stored.
/// <para>
/// The curve is sublinear on purpose. A flat "N chapters per level" makes the number meaningless at
/// the top and unreachable at the bottom; a steep exponential stalls after a few weeks. Cumulative
/// cost is <c>350 * (L-1)^(4/3)</c>, which puts the marginal cost of a level at roughly
/// <c>467 * (L-1)^(1/3)</c> XP — about five chapters early on, ten around level 10, seventeen around
/// 50 and thirty by 270. There is always a next level in sight, and it never becomes free.
/// </para>
/// <para>
/// The <c>L-1</c> is not cosmetic. Anchoring the curve at level 1 rather than special-casing it to
/// zero is what keeps the first level-up the cheapest one: with a plain <c>L^(4/3)</c> the jump from
/// 1 to 2 swallows the whole <c>350 * 1</c> base and ends up costing more than the jump from 2 to 3,
/// so a brand-new reader waits longer for their first level than for their second.
/// </para>
/// <para>
/// Calibration, pinned by <c>LevelMathTests</c> so a later retune cannot quietly flatten it: a reader
/// with ~500 chapters and ~100 hours lands near level 49, and ~5,000 chapters with ~1,000 hours near
/// level 274.
/// </para>
/// </summary>
public static class LevelMath
{
    public const int XpPerChapter = 100;
    public const int XpPerMinuteRead = 2;
    public const int XpPerSeriesFinished = 2500;

    /// <summary>
    /// What each achievement tier is worth, indexed by <c>tier - 1</c>. Badges are a small share of a
    /// serious reader's total: they recognise the milestone, they are not the way to level.
    /// </summary>
    public static readonly int[] XpPerTier = [500, 1500, 3000, 6000, 12000, 25000];

    /// <summary>Cumulative XP scale. Raising this makes every level cost proportionally more.</summary>
    private const double LevelScale = 350d;

    /// <summary>
    /// Curve steepness. 4/3 is the whole shape of the progression; see the type's remarks before
    /// touching it.
    /// </summary>
    private const double LevelExponent = 4d / 3d;

    /// <summary>
    /// Volumes count as chapters here. A volume-only series (no chapter numbering) emits
    /// <c>VolumesRead</c> instead of <c>ChaptersRead</c>, and reading one is plainly not worth less
    /// than reading a single chapter — but it is the only signal that series produces, so counting it
    /// at the chapter rate is the honest floor rather than an attempt to guess how many chapters it
    /// contained.
    /// </summary>
    public static long Xp(long chaptersRead, long volumesRead, long readingSeconds, long seriesFinished,
        IEnumerable<int> unlockedTiers)
    {
        var xp = (chaptersRead + volumesRead) * XpPerChapter
                 + readingSeconds / 60 * XpPerMinuteRead
                 + seriesFinished * (long)XpPerSeriesFinished;

        foreach (var tier in unlockedTiers)
        {
            if (tier >= 1 && tier <= XpPerTier.Length)
            {
                xp += XpPerTier[tier - 1];
            }
        }

        return Math.Max(0, xp);
    }

    /// <summary>Cumulative XP needed to reach <paramref name="level"/>. Level 1 costs nothing.</summary>
    public static long XpForLevel(int level) =>
        level <= 1 ? 0 : (long)Math.Floor(LevelScale * Math.Pow(level - 1, LevelExponent));

    /// <summary>
    /// The inverse of <see cref="XpForLevel"/>. Floors, and never returns below 1 — somebody who has
    /// read nothing is level 1, not level 0.
    /// </summary>
    public static int LevelForXp(long xp)
    {
        if (xp <= 0)
        {
            return 1;
        }

        var level = 1 + (int)Math.Floor(Math.Pow(xp / LevelScale, 1d / LevelExponent));

        // Floating-point pow is not exactly the inverse of floating-point pow. Nudge onto the right
        // side of the boundary so the level and the "XP into this level" figure can never disagree
        // by a level, which would render a progress bar past 100% or below 0.
        while (level > 1 && XpForLevel(level) > xp)
        {
            level--;
        }

        while (XpForLevel(level + 1) <= xp)
        {
            level++;
        }

        return Math.Max(1, level);
    }

    /// <summary>
    /// A level plus where the reader sits inside it, which is everything a progress ring needs.
    /// <paramref name="Progress"/> is 0..1.
    /// </summary>
    public readonly record struct LevelProgress(int Level, long Xp, long LevelStartXp, long NextLevelXp)
    {
        public long IntoLevel => Xp - LevelStartXp;
        public long LevelSpan => Math.Max(1, NextLevelXp - LevelStartXp);
        public double Progress => Math.Clamp((double)IntoLevel / LevelSpan, 0, 1);
    }

    public static LevelProgress Progress(long xp)
    {
        var level = LevelForXp(xp);
        return new LevelProgress(level, xp, XpForLevel(level), XpForLevel(level + 1));
    }
}
