using Maki.Core.Progress;

namespace Maki.Core.Tests;

public class LevelMathTests
{
    private static long Xp(long chapters, long hours, long finished = 0) =>
        LevelMath.Xp(chapters, 0, hours * 3600, finished, []);

    [Fact]
    public void NobodyStartsBelowLevelOne()
    {
        Assert.Equal(1, LevelMath.LevelForXp(0));
        Assert.Equal(1, LevelMath.LevelForXp(-5));
        Assert.Equal(0, LevelMath.XpForLevel(1));
    }

    [Fact]
    public void LevelForXpInvertsXpForLevel()
    {
        // The two are floating-point functions of each other, so exactly-on-the-boundary XP is the
        // case that breaks first: landing a level low there renders a progress ring past 100%.
        for (var level = 1; level <= 400; level++)
        {
            var at = LevelMath.XpForLevel(level);
            Assert.Equal(level, LevelMath.LevelForXp(at));
            Assert.Equal(level, LevelMath.LevelForXp(at + 1));
            if (level > 1)
            {
                Assert.Equal(level - 1, LevelMath.LevelForXp(at - 1));
            }
        }
    }

    [Fact]
    public void CostPerLevelOnlyEverRises()
    {
        long previous = 0;
        for (var level = 2; level <= 300; level++)
        {
            var cost = LevelMath.XpForLevel(level) - LevelMath.XpForLevel(level - 1);
            Assert.True(cost >= previous, $"level {level} cost {cost} fell below {previous}");
            previous = cost;
        }
    }

    [Fact]
    public void ProgressStaysInsideItsLevel()
    {
        foreach (var xp in new long[] { 0, 1, 500, 12_345, 62_000, 620_000, 5_000_000 })
        {
            var p = LevelMath.Progress(xp);
            Assert.InRange(p.Progress, 0, 1);
            Assert.True(p.IntoLevel >= 0);
            Assert.True(p.NextLevelXp > p.LevelStartXp);
        }
    }

    /// <summary>
    /// Pins the shape of the curve, not just that it is monotonic. A retune that flattens
    /// progression back out would still satisfy every other test here, and flat progression is the
    /// exact thing this curve was chosen to avoid.
    /// </summary>
    [Fact]
    public void ProgressionIsCalibratedForRealReaders()
    {
        // ~500 chapters and ~100 hours: someone a year or two in.
        Assert.InRange(LevelMath.LevelForXp(Xp(500, 100)), 45, 52);

        // ~5,000 chapters and ~1,000 hours: a heavy multi-year reader.
        Assert.InRange(LevelMath.LevelForXp(Xp(5000, 1000)), 260, 290);
    }

    [Fact]
    public void TheFirstLevelArrivesWithinAHandfulOfChapters()
    {
        // The point of the curve: a new reader sees the number move almost immediately.
        Assert.True(LevelMath.LevelForXp(Xp(10, 0)) >= 2);
    }

    [Fact]
    public void VolumesCountLikeChapters()
    {
        Assert.Equal(
            LevelMath.Xp(4, 0, 0, 0, []),
            LevelMath.Xp(0, 4, 0, 0, []));
    }

    [Fact]
    public void UnknownTiersContributeNothing()
    {
        var baseline = LevelMath.Xp(10, 0, 0, 0, []);
        Assert.Equal(baseline, LevelMath.Xp(10, 0, 0, 0, [0, -1, 99]));
        Assert.True(LevelMath.Xp(10, 0, 0, 0, [1]) > baseline);
    }
}
