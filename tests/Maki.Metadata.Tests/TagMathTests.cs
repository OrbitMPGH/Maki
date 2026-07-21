using System.Reflection;
using Maki.Metadata.Embedding;
using Xunit;

namespace Maki.Metadata.Tests;

public class TagMathTests
{
    private static double FlatIdf(int _) => 1.0;

    [Fact]
    public void ClassOf_MapsAllWeightStrings()
    {
        Assert.Equal(TagMath.Core, TagMath.ClassOf("core"));
        Assert.Equal(TagMath.Defining, TagMath.ClassOf("defining"));
        Assert.Equal(TagMath.Recurrent, TagMath.ClassOf("recurrent"));
        Assert.Equal(TagMath.Incidental, TagMath.ClassOf("incidental"));
        Assert.Equal(TagMath.Unweighted, TagMath.ClassOf("unweighted"));
        Assert.Equal(TagMath.Unweighted, TagMath.ClassOf(null));
        Assert.Equal(TagMath.Unweighted, TagMath.ClassOf("garbage"));
    }

    [Fact]
    public void ClassWeight_OrdersByStrength()
    {
        Assert.True(TagMath.ClassWeight(TagMath.Core) > TagMath.ClassWeight(TagMath.Defining));
        Assert.True(TagMath.ClassWeight(TagMath.Defining) > TagMath.ClassWeight(TagMath.Recurrent));
        Assert.True(TagMath.ClassWeight(TagMath.Recurrent) > TagMath.ClassWeight(TagMath.Incidental));
        // Unweighted means "not rated", not "irrelevant" — it sits between incidental and recurrent.
        Assert.True(TagMath.ClassWeight(TagMath.Unweighted) > TagMath.ClassWeight(TagMath.Incidental));
    }

    [Fact]
    public void PackUnpack_RoundTrips()
    {
        var tags = new List<(int, byte)> { (1, TagMath.Core), (70000, TagMath.Incidental), (42, TagMath.Unweighted) };
        Assert.Equal(tags, TagMath.Unpack(TagMath.Pack(tags)));
    }

    [Fact]
    public void Unpack_BadInput_IsEmpty()
    {
        Assert.Empty(TagMath.Unpack(null));
        Assert.Empty(TagMath.Unpack([]));
        Assert.Empty(TagMath.Unpack([1, 2, 3])); // not a multiple of the entry size
    }

    [Fact]
    public void Score_IdenticalTags_IsOne()
    {
        var blob = TagMath.Pack([(1, TagMath.Core), (2, TagMath.Defining)]);
        var profile = TagMath.BuildProfile([blob], FlatIdf);
        Assert.Equal(1.0, TagMath.Score(blob, profile, FlatIdf), 6);
    }

    [Fact]
    public void Score_DisjointTags_IsZero()
    {
        var profile = TagMath.BuildProfile([TagMath.Pack([(1, TagMath.Core)])], FlatIdf);
        Assert.Equal(0.0, TagMath.Score(TagMath.Pack([(2, TagMath.Core)]), profile, FlatIdf));
    }

    [Fact]
    public void Score_EmptyProfileOrNullCandidate_IsZero()
    {
        var blob = TagMath.Pack([(1, TagMath.Core)]);
        Assert.Equal(0.0, TagMath.Score(blob, TagMath.Profile.Empty, FlatIdf));
        Assert.Equal(0.0, TagMath.Score(null, TagMath.BuildProfile([blob], FlatIdf), FlatIdf));
    }

    [Fact]
    public void Score_CoreMatchBeatsIncidentalMatch()
    {
        // Seed loves tags 1 and 2 equally; candidate A shares the pair as core themes,
        // candidate B only incidentally. A must score higher.
        var profile = TagMath.BuildProfile([TagMath.Pack([(1, TagMath.Core), (2, TagMath.Core)])], FlatIdf);
        var strong = TagMath.Score(TagMath.Pack([(1, TagMath.Core), (2, TagMath.Core), (3, TagMath.Core)]), profile, FlatIdf);
        var weak = TagMath.Score(TagMath.Pack([(1, TagMath.Incidental), (2, TagMath.Incidental), (3, TagMath.Core)]), profile, FlatIdf);
        Assert.True(strong > weak);
    }

    [Fact]
    public void Score_RareSharedTagBeatsCommonOne()
    {
        // Two seeds → profile has tags 1 (rare) and 2 (very common) at equal class weight.
        // A candidate sharing only the rare tag must beat one sharing only the common tag.
        double Idf(int id) => id == 1 ? 5.0 : 0.5;
        var profile = TagMath.BuildProfile([TagMath.Pack([(1, TagMath.Core), (2, TagMath.Core)])], Idf);
        var rare = TagMath.Score(TagMath.Pack([(1, TagMath.Core), (9, TagMath.Core)]), profile, Idf);
        var common = TagMath.Score(TagMath.Pack([(2, TagMath.Core), (9, TagMath.Core)]), profile, Idf);
        Assert.True(rare > common);
    }

    [Fact]
    public void BuildProfile_AveragesAcrossSeeds()
    {
        // Tag 1 in both seeds (core), tag 2 only in one — 1 should carry twice the weight.
        var profile = TagMath.BuildProfile(
            [TagMath.Pack([(1, TagMath.Core)]), TagMath.Pack([(1, TagMath.Core), (2, TagMath.Core)])],
            FlatIdf);
        Assert.Equal(2.0, profile.IdfWeight[1] / profile.IdfWeight[2], 6);
    }

    [Fact]
    public void Score_ReportsMatchedContributions()
    {
        var profile = TagMath.BuildProfile([TagMath.Pack([(1, TagMath.Core), (2, TagMath.Incidental)])], FlatIdf);
        var matched = new List<(int Id, double Contribution)>();
        TagMath.Score(TagMath.Pack([(1, TagMath.Core), (2, TagMath.Core), (3, TagMath.Core)]), profile, FlatIdf, matched);
        Assert.Equal(2, matched.Count);
        var byId = matched.ToDictionary(m => m.Id, m => m.Contribution);
        Assert.True(byId[1] > byId[2]); // the core↔core match contributes more than core↔incidental
    }

    [Fact]
    public void Score_PenalisesHeavilyTaggedSeries()
    {
        // Documents *why* search doesn't use this scorer. Both series carry every tag the profile
        // asks for, but the cosine divides by the candidate's own norm, so the richly-tagged one
        // scores far lower — and richly-tagged is exactly what the famous titles are (Berserk
        // carries 203 tags). See SemanticSearcher.ScoreAgainstQueryTags.
        var profile = TagMath.BuildProfile([TagMath.Pack([(1, TagMath.Core), (2, TagMath.Core)])], FlatIdf);
        var sparse = TagMath.Pack([(1, TagMath.Core), (2, TagMath.Core)]);
        var rich = TagMath.Pack(
            Enumerable.Range(1, 60).Select(id => (id, id <= 2 ? TagMath.Core : TagMath.Recurrent)).ToList());

        // 1.00 vs 0.42 with these inputs: the same matches, less than half the score.
        Assert.True(TagMath.Score(sparse, profile, FlatIdf) > TagMath.Score(rich, profile, FlatIdf) * 2);
    }

    [Fact]
    public void SearchScorer_IsIndifferentToHowElseATagIsTagged()
    {
        // The search-side scorer answers "how much of what the query asked for is present", so
        // carrying 58 unrelated tags costs nothing.
        var profile = TagMath.BuildProfile([TagMath.Pack([(1, TagMath.Core), (2, TagMath.Core)])], FlatIdf);
        var sparse = TagMath.Pack([(1, TagMath.Core), (2, TagMath.Core)]);
        var rich = TagMath.Pack(
            Enumerable.Range(1, 60).Select(id => (id, id <= 2 ? TagMath.Core : TagMath.Recurrent)).ToList());

        Assert.Equal(SearchScore(sparse, profile), SearchScore(rich, profile), 6);
    }

    /// <summary>Invokes SemanticSearcher's private search-side tag scorer.</summary>
    private static double SearchScore(byte[] blob, TagMath.Profile profile)
    {
        var method = typeof(SemanticSearcher).GetMethod(
            "ScoreAgainstQueryTags", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (double)method.Invoke(null, [blob, profile, FlatIdf])!;
    }
}
