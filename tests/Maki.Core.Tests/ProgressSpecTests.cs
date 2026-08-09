using Maki.Core.Configuration;

namespace Maki.Core.Tests;

public class ProgressSpecTests
{
    [Fact]
    public void UnsetReadsAsOnStreaksShownNotShared()
    {
        foreach (var json in new[] { null, "", "   " })
        {
            var spec = ProgressSpec.Parse(json);
            Assert.True(spec.Enabled);
            Assert.True(spec.ShowStreaks);
            Assert.False(spec.ShowOnLeaderboard);
        }
    }

    [Fact]
    public void SharingIsNeverInferred()
    {
        // Being listed for other people is opt-in and must stay that way: a shape that silently
        // defaulted it on would publish somebody's numbers on upgrade.
        Assert.False(ProgressSpec.Parse("{}").ShowOnLeaderboard);
        Assert.False(ProgressSpec.Default.ShowOnLeaderboard);
    }

    [Fact]
    public void BadJsonFallsBackRatherThanThrowing()
    {
        var spec = ProgressSpec.Parse("{ not json at all");
        Assert.Equal(ProgressSpec.Default, spec);
    }

    [Fact]
    public void RoundTripsThroughItsOwnOptions()
    {
        var spec = new ProgressSpec(Enabled: false, ShowStreaks: false, ShowOnLeaderboard: true);
        Assert.Equal(spec, ProgressSpec.Parse(ProgressSpec.Serialize(spec)));
    }

    [Fact]
    public void SerializesCamelCase()
    {
        Assert.Contains("showOnLeaderboard", ProgressSpec.Serialize(ProgressSpec.Default));
    }

    [Fact]
    public void ReadsCaseInsensitively()
    {
        var spec = ProgressSpec.Parse("""{"Enabled":false,"SHOWSTREAKS":false}""");
        Assert.False(spec.Enabled);
        Assert.False(spec.ShowStreaks);
    }

    [Fact]
    public void UnknownPropertiesAreIgnored()
    {
        // Forward compatibility: a blob written by a newer build must not blank the page on the old
        // one. The mirror image, a property this build knows that the blob lacks, falls back to the
        // record's parameter default.
        var spec = ProgressSpec.Parse("""{"enabled":false,"somethingNewer":42}""");
        Assert.False(spec.Enabled);
        Assert.True(spec.ShowStreaks);
    }
}
