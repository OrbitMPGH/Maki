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
    public void BuildProfile_WeightsSeedsWhenTheCallerSaysTo()
    {
        // The tag channel carries the second-largest coefficient in the hybrid score, and until this
        // overload existed it was a flat mean: a series read to the end and one opened once shaped it
        // identically, however hard behavioural weighting had pushed them apart.
        var profile = TagMath.BuildProfile(
            [(TagMath.Pack([(1, TagMath.Core)]), 3.0), (TagMath.Pack([(2, TagMath.Core)]), 1.0)],
            FlatIdf);
        Assert.Equal(3.0, profile.IdfWeight[1] / profile.IdfWeight[2], 6);
    }

    [Fact]
    public void BuildProfile_IsUnchangedByScalingEveryWeightTogether()
    {
        // The profile is normalized into a cosine, so only relative weights can matter. A caller
        // handing in ratings on a 0-10 scale and one handing in the same taste on 0-100 must get the
        // identical profile, or every weight source would need its own calibration.
        var small = TagMath.BuildProfile(
            [(TagMath.Pack([(1, TagMath.Core)]), 0.4), (TagMath.Pack([(2, TagMath.Core)]), 1.8)], FlatIdf);
        var large = TagMath.BuildProfile(
            [(TagMath.Pack([(1, TagMath.Core)]), 40.0), (TagMath.Pack([(2, TagMath.Core)]), 180.0)], FlatIdf);

        Assert.Equal(small.IdfWeight[1], large.IdfWeight[1], 9);
        Assert.Equal(small.IdfWeight[2], large.IdfWeight[2], 9);
    }

    [Fact]
    public void BuildProfile_TreatsEveryWeightZeroAsNoProfile()
    {
        // Not a fallback to a flat mean. A caller that weighted everything to nothing has said the
        // seeds carry no signal, and quietly averaging them anyway would ignore it.
        Assert.True(TagMath.BuildProfile(
            [(TagMath.Pack([(1, TagMath.Core)]), 0.0), (TagMath.Pack([(2, TagMath.Core)]), 0.0)],
            FlatIdf).IsEmpty);
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
        return (double)method.Invoke(null, [blob, profile, (Func<int, double>)FlatIdf])!;
    }

    [Fact]
    public void Sharpening_ConcentratesTheProfileOnWhatTheSeedsAgreeAbout()
    {
        // The failure this exists for: Score is a cosine over the candidate's whole tag list, so a
        // candidate matching several middling profile tags beats one matching the top two. Seeds
        // that all share one premise tag and disagree about everything else should not lose to a
        // candidate that shares none of the premise and half of the noise.
        var shared = TagMath.Pack([(1, TagMath.Core), (2, TagMath.Core), (3, TagMath.Core)]);
        var noisy = TagMath.Pack([(1, TagMath.Core), (4, TagMath.Core), (5, TagMath.Core), (6, TagMath.Core)]);
        var seeds = new[] { TagMath.Pack([(1, TagMath.Core), (4, TagMath.Core)]), TagMath.Pack([(1, TagMath.Core), (5, TagMath.Core)]), TagMath.Pack([(1, TagMath.Core), (6, TagMath.Core)]) };

        var flat = TagMath.BuildProfile([.. seeds.Select(b => (b, 1.0))], FlatIdf);
        var sharp = TagMath.BuildProfile([.. seeds.Select(b => (b, 1.0))], FlatIdf, sharpening: 3.0);

        // Tag 1 is on every seed; 4, 5 and 6 are on one each, so the flat profile ranks 1 highest
        // already. Sharpening does not reorder it, it widens the gap.
        Assert.True(flat.IdfWeight[1] > flat.IdfWeight[4]);
        Assert.True(
            sharp.IdfWeight[1] / sharp.IdfWeight[4] > flat.IdfWeight[1] / flat.IdfWeight[4],
            "sharpening should widen the ratio between an agreed tag and a one-seed tag");

        // And the consequence that matters: the candidate carrying the agreed tag overtakes the one
        // carrying more of the tail.
        Assert.True(TagMath.Score(noisy, flat, FlatIdf) > TagMath.Score(shared, flat, FlatIdf));
        Assert.True(TagMath.Score(shared, sharp, FlatIdf) > TagMath.Score(noisy, sharp, FlatIdf));
    }

    [Fact]
    public void Sharpening_OfOne_IsTheProfileUnchanged()
    {
        // The identity has to be exact, not merely close: it is what makes the knob safe to leave
        // alone and what the eval's baseline variant relies on.
        var seeds = new[] { TagMath.Pack([(1, TagMath.Core), (2, TagMath.Core)]), TagMath.Pack([(2, TagMath.Core), (3, TagMath.Core)]) };
        static double Idf(int id) => 1.0 + id;

        var implicitOne = TagMath.BuildProfile([.. seeds.Select(b => (b, 1.0))], Idf);
        var explicitOne = TagMath.BuildProfile([.. seeds.Select(b => (b, 1.0))], Idf, sharpening: 1.0);

        Assert.Equal(implicitOne.Norm, explicitOne.Norm);
        Assert.Equal(implicitOne.IdfWeight, explicitOne.IdfWeight);
    }

    [Fact]
    public void CategoryWeight_LiftsStoryTagsOnly_AndTreatsAnUncategorisedTagAsNeutral()
    {
        Assert.Equal(3.0, TagMath.CategoryWeight("Themes", 3.0));
        Assert.Equal(3.0, TagMath.CategoryWeight("Relationship", 3.0));
        Assert.Equal(1.0, TagMath.CategoryWeight("Character Traits", 3.0));
        Assert.Equal(1.0, TagMath.CategoryWeight("Sexual Content", 3.0));

        // Measured out of the set rather than reasoned out: both read as story-bearing, and both
        // carry the tags that were promoting a generic romcom over the premise matches.
        Assert.Equal(1.0, TagMath.CategoryWeight("Narrative Tropes", 3.0));
        Assert.Equal(1.0, TagMath.CategoryWeight("Locations", 3.0));

        // A vocabulary written before the column existed reports nothing, and must degrade to the
        // old behaviour rather than to a lopsided one where half the tags got lifted.
        Assert.Equal(1.0, TagMath.CategoryWeight("", 3.0));
        Assert.Equal(1.0, TagMath.CategoryWeight(null, 3.0));

        // A boost of 1 is the identity for every category, so the knob is genuinely off at 1.
        Assert.Equal(1.0, TagMath.CategoryWeight("Themes", 1.0));
    }

    [Fact]
    public void CategoryWeight_PrefersTheSharedPremise_OverTheSharedGenreTropes()
    {
        // The case this exists for, reduced to its bones. Tag 1 is the premise the seeds share
        // ("Themes > Cohabitation"); tags 4, 5 and 6 are the trope tail their whole genre shares
        // ("Character Traits", "Sexual Content"). One candidate has the premise and nothing else,
        // the other has three tropes and no premise.
        //
        // IDF cannot separate them and is deliberately set to make that concrete here: the tropes
        // are RARER than the premise, exactly as Dense Male Lead is rarer than Cohabitation in the
        // real vocabulary, so rarity-based weighting actively prefers the wrong candidate.
        var premise = TagMath.Pack([(1, TagMath.Core)]);
        var tropes = TagMath.Pack([(4, TagMath.Core), (5, TagMath.Core), (6, TagMath.Core)]);
        var seeds = new[]
        {
            TagMath.Pack([(1, TagMath.Core), (4, TagMath.Core)]),
            TagMath.Pack([(1, TagMath.Core), (5, TagMath.Core)]),
            TagMath.Pack([(1, TagMath.Core), (6, TagMath.Core)]),
        };

        static double Idf(int id) => id == 1 ? 1.0 : 2.0;
        static double Category(int id) => id == 1 ? 4.0 : 1.0;

        var flat = TagMath.BuildProfile([.. seeds.Select(b => (b, 1.0))], Idf);
        var weighted = TagMath.BuildProfile(
            [.. seeds.Select(b => (b, 1.0))], Idf, categoryWeight: Category);

        Assert.True(
            TagMath.Score(tropes, flat, Idf) > TagMath.Score(premise, flat, Idf),
            "without categories the trope-matcher should win, which is the bug");
        Assert.True(
            TagMath.Score(premise, weighted, Idf, categoryWeight: Category) >
            TagMath.Score(tropes, weighted, Idf, categoryWeight: Category),
            "weighting the story category should hand it to the premise-matcher");
    }

    [Fact]
    public void CategoryWeight_OfOne_LeavesScoringExactlyWhereItWas()
    {
        // The knob's off position has to be bit-identical, not merely close: it is the eval's
        // baseline and the behaviour every install keeps until the vocabulary is rebuilt.
        var seeds = new[]
        {
            TagMath.Pack([(1, TagMath.Core), (2, TagMath.Defining)]),
            TagMath.Pack([(2, TagMath.Core), (3, TagMath.Core)]),
        };
        var candidate = TagMath.Pack([(2, TagMath.Core), (3, TagMath.Incidental)]);
        static double Idf(int id) => 1.0 + id;
        static double Off(int id) => TagMath.CategoryWeight("Themes", 1.0);

        var plain = TagMath.BuildProfile([.. seeds.Select(b => (b, 1.0))], Idf);
        var offKnob = TagMath.BuildProfile([.. seeds.Select(b => (b, 1.0))], Idf, categoryWeight: Off);

        Assert.Equal(plain.IdfWeight, offKnob.IdfWeight);
        Assert.Equal(plain.Norm, offKnob.Norm);
        Assert.Equal(
            TagMath.Score(candidate, plain, Idf),
            TagMath.Score(candidate, offKnob, Idf, categoryWeight: Off));
    }

    [Fact]
    public void Consensus_WidensTheGapBetweenAnAgreedTagAndAHalfAgreedOne()
    {
        // What the knob actually does, stated as the ratio it moves rather than as a claim about
        // which candidate wins - the cosine already handles the simple cases, and an earlier version
        // of this test asserted a failure mode that does not exist.
        //
        // Tag 1 is on every seed, tag 2 on half of them. Linear pricing puts them at 2:1; raising
        // the power pushes the half-agreed tag toward nothing while leaving the unanimous one alone,
        // so a seed set's shared core stops competing with whatever half of it happened to have.
        var seeds = new[]
        {
            TagMath.Pack([(1, TagMath.Core), (2, TagMath.Core)]),
            TagMath.Pack([(1, TagMath.Core), (2, TagMath.Core)]),
            TagMath.Pack([(1, TagMath.Core)]),
            TagMath.Pack([(1, TagMath.Core)]),
        };

        var linear = TagMath.BuildProfile([.. seeds.Select(b => (b, 1.0))], FlatIdf);
        var squared = TagMath.BuildProfile([.. seeds.Select(b => (b, 1.0))], FlatIdf, consensus: 2.0);
        var cubed = TagMath.BuildProfile([.. seeds.Select(b => (b, 1.0))], FlatIdf, consensus: 3.0);

        double Ratio(TagMath.Profile p) => p.IdfWeight[1] / p.IdfWeight[2];

        Assert.Equal(2.0, Ratio(linear), 6);
        Assert.Equal(4.0, Ratio(squared), 6);
        Assert.Equal(8.0, Ratio(cubed), 6);
        // The unanimous tag is untouched in absolute terms; only the partial one is pushed down.
        Assert.Equal(linear.IdfWeight[1], cubed.IdfWeight[1], 6);
    }

    [Fact]
    public void Consensus_CarriesNoRarity_WhichIsWhatSeparatesItFromSharpening()
    {
        // The distinction that matters, since sharpening had to be abandoned for rewarding rare
        // tags. Two tags with identical seed agreement and wildly different IDF must keep their
        // ratio exactly when consensus is raised: it reweights agreement and nothing else.
        var seeds = new[]
        {
            TagMath.Pack([(1, TagMath.Core), (2, TagMath.Core)]),
            TagMath.Pack([(1, TagMath.Core), (2, TagMath.Core)]),
        };
        static double Lopsided(int id) => id == 1 ? 0.5 : 8.0;

        var linear = TagMath.BuildProfile([.. seeds.Select(b => (b, 1.0))], Lopsided);
        var consensus = TagMath.BuildProfile([.. seeds.Select(b => (b, 1.0))], Lopsided, consensus: 4.0);

        Assert.Equal(
            linear.IdfWeight[2] / linear.IdfWeight[1],
            consensus.IdfWeight[2] / consensus.IdfWeight[1],
            6);
    }

    [Fact]
    public void Consensus_OfOne_LeavesTheProfileExactlyWhereItWas()
    {
        var seeds = new[]
        {
            TagMath.Pack([(1, TagMath.Core), (2, TagMath.Defining)]),
            TagMath.Pack([(2, TagMath.Core), (3, TagMath.Incidental)]),
        };
        static double Idf(int id) => 1.0 + id;

        var plain = TagMath.BuildProfile([.. seeds.Select(b => (b, 1.0))], Idf);
        var explicitOne = TagMath.BuildProfile([.. seeds.Select(b => (b, 1.0))], Idf, consensus: 1.0);

        Assert.Equal(plain.IdfWeight, explicitOne.IdfWeight);
        Assert.Equal(plain.Norm, explicitOne.Norm);
    }

    // ---------------------------------------------------------------------------------------------
    // TagTree: taxonomy ancestors
    // ---------------------------------------------------------------------------------------------

    private static Dictionary<int, TagInfo> TreeVocab() => new()
    {
        // Two siblings under one parent, plus the parent itself as a real tag, plus an unrelated
        // branch. Counts are chosen so the parent is common and the leaves are rare.
        [1] = new TagInfo("Swordplay", 200, false, "Activities", "Activities > Physical > Swordplay"),
        [2] = new TagInfo("Martial Arts", 300, false, "Activities", "Activities > Physical > Martial Arts"),
        [3] = new TagInfo("Physical", 9000, false, "Activities", "Activities > Physical"),
        [4] = new TagInfo("Cooking", 250, false, "Themes", "Themes > Food > Cooking"),
    };

    [Fact]
    public void TagTree_IsEmptyAtZeroDecay_SoTheChannelScoresExactlyAsBefore()
    {
        var off = TagMath.TagTree.Build(TreeVocab(), decay: 0, includeSelf: false, activeCount: 10_000);
        Assert.True(off.IsEmpty);

        var blob = TagMath.Pack([(1, TagMath.Core)]);
        var profile = TagMath.BuildProfile([(blob, 1.0)], Idf, tree: off);
        // Byte-identical to passing no tree at all, which is what makes the default free.
        var withoutTree = TagMath.BuildProfile([(blob, 1.0)], Idf);
        Assert.Equal(withoutTree.Norm, profile.Norm, 12);
        Assert.Equal(
            TagMath.Score(blob, withoutTree, Idf),
            TagMath.Score(blob, profile, Idf, tree: off), 12);

        static double Idf(int id) => 2.0;
    }

    [Fact]
    public void TagTree_LetsSiblingsMatchThroughTheirSharedParent()
    {
        var vocab = TreeVocab();
        double Idf(int id) => vocab.TryGetValue(id, out var t) ? Math.Log(10_000.0 / t.SeriesCount) : 1.0;
        var tree = TagMath.TagTree.Build(vocab, decay: 0.5, includeSelf: false, activeCount: 10_000);

        var seed = TagMath.Pack([(1, TagMath.Core)]);          // Swordplay
        var sibling = TagMath.Pack([(2, TagMath.Core)]);        // Martial Arts, shares the parent
        var stranger = TagMath.Pack([(4, TagMath.Core)]);       // Cooking, different root

        var profile = TagMath.BuildProfile([(seed, 1.0)], Idf, tree: tree);

        // Exact-id matching scores both at zero; this is the sparsity the tree exists to fix.
        Assert.Equal(0, TagMath.Score(sibling, TagMath.BuildProfile([(seed, 1.0)], Idf), Idf));

        var siblingScore = TagMath.Score(sibling, profile, Idf, tree: tree);
        var strangerScore = TagMath.Score(stranger, profile, Idf, tree: tree);
        Assert.True(siblingScore > 0, "a sibling should match through the shared parent");
        Assert.Equal(0, strangerScore);
    }

    [Fact]
    public void TagTree_PricesAnAncestorByItsOwnRarity_NotByTheTagThatImpliedIt()
    {
        // THE BUG THIS PINS cost a whole tuning round. Giving an ancestor the IDF of the child that
        // reached it prices a near-ubiquitous parent as if it were as rare as its rarest leaf. The
        // error compounds with seed count, and it measured as a large single-seed nDCG gain while
        // simultaneously collapsing whole-library results into a popularity chart.
        var vocab = TreeVocab();
        var tree = TagMath.TagTree.Build(vocab, decay: 0.5, includeSelf: false, activeCount: 10_000);

        // Decay 0.5 at one level up identifies the immediate parent, "Activities > Physical".
        var parent = Assert.Single(tree.AncestorsOf(1).Where(a => a.Decay == 0.5));
        Assert.True(parent.Id < 0, "ancestor ids are negative so they cannot collide with tag ids");

        // "Activities > Physical" is a real tag carrying 9,000 of 10,000 series, so its IDF is tiny.
        var parentIdf = Math.Log(10_000.0 / 9_000.0);
        Assert.Equal(parentIdf, parent.Idf, 6);

        // The leaf that implied it is rare, and its IDF is an order of magnitude larger. Inheriting
        // that number is precisely the defect.
        Assert.True(Math.Log(10_000.0 / 200.0) > parent.Idf * 10);
    }

    [Fact]
    public void TagTree_DecaysPerLevel()
    {
        var tree = TagMath.TagTree.Build(TreeVocab(), decay: 0.5, includeSelf: false, activeCount: 10_000);

        // "Activities > Physical > Swordplay" is depth 3: its parent is one level up (0.5) and the
        // root two (0.25).
        var ancestors = tree.AncestorsOf(1).OrderBy(a => a.Decay).ToList();
        Assert.Equal(2, ancestors.Count);
        Assert.Equal(0.25, ancestors[0].Decay, 6);
        Assert.Equal(0.5, ancestors[1].Decay, 6);
    }

    [Fact]
    public void TagTree_IncludesSelfOnlyWhenAsked()
    {
        var without = TagMath.TagTree.Build(TreeVocab(), 0.5, includeSelf: false, activeCount: 10_000);
        var with = TagMath.TagTree.Build(TreeVocab(), 0.5, includeSelf: true, activeCount: 10_000);

        Assert.Equal(2, without.AncestorsOf(1).Length);
        // The tag's own full path joins as a third node at weight 1, which is what lets a series
        // tagged with the PARENT meet one tagged with the child at the parent's own path.
        Assert.Equal(3, with.AncestorsOf(1).Length);
        Assert.Contains(with.AncestorsOf(1), a => a.Decay == 1.0);
    }

    [Fact]
    public void TagTree_GivesAncestorsNegativeIds_SoTheUiCanNeverNameThem()
    {
        var vocab = TreeVocab();
        var tree = TagMath.TagTree.Build(vocab, 0.5, includeSelf: true, activeCount: 10_000);

        // SemanticRecommender builds its "matched tags" list by looking every contributing id up in
        // the vocabulary and dropping what it cannot name. Negative ids are what makes that filter
        // correct by construction rather than by a rule someone has to remember.
        foreach (var ancestor in tree.AncestorsOf(1))
        {
            Assert.True(ancestor.Id < 0);
            Assert.False(vocab.ContainsKey(ancestor.Id));
        }
    }

    [Fact]
    public void TagTree_IgnoresAVocabularyWithNoPaths_SoAStaleIndexDegradesCleanly()
    {
        // An index written before the name_path column exists reads it as empty. That has to mean
        // "no ancestors", never "one giant ancestor everything shares".
        var stale = new Dictionary<int, TagInfo>
        {
            [1] = new TagInfo("Swordplay", 200, false, "Activities"),
            [2] = new TagInfo("Martial Arts", 300, false, "Activities"),
        };

        var tree = TagMath.TagTree.Build(stale, decay: 0.5, includeSelf: false, activeCount: 10_000);
        Assert.True(tree.IsEmpty);
    }
}
