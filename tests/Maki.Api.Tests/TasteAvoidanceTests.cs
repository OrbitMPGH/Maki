using Maki.Api.Services;

namespace Maki.Api.Tests;

/// <summary>
/// Which facets of a reader's pushed-down titles may be named back at them.
///
/// <para>
/// Synthetic facet sets, the same way <c>TasteGroupMiningTests</c> works: every question here is
/// about the three thresholds, not about where the tags came from. The visibility rules are the seed
/// snapshot's and are covered where they live, so an ignored source never reaches this arithmetic.
/// </para>
/// </summary>
public class TasteAvoidanceTests
{
    /// <summary>A catalogue big enough for an IDF to mean something.</summary>
    private const long Corpus = 126_000;

    /// <summary>On a fiftieth of the catalogue, which is comfortably specific.</summary>
    private const long NarrowDf = Corpus / 50;

    /// <summary>On a third of it, like "School" or "Japan": a backdrop nobody has an opinion about.</summary>
    private const long BroadDf = Corpus / 3;

    private static IReadOnlyDictionary<long, string> Titles(int count) =>
        Enumerable.Range(1, count).ToDictionary(i => (long)i, i => $"Avoided {i}");

    private static TasteAvoidanceService.Candidate Facet(
        string name, long df, params long[] carriers) => new(name, true, df, carriers);

    [Fact]
    public void Two_dislikes_sharing_a_theme_name_nothing()
    {
        var labels = TasteAvoidanceService.Select(
            [Facet("Harem", NarrowDf, 1, 2)], Titles(2), Corpus);

        // Two is a coincidence a reader would read as a rule, and the ranking has not acted on a
        // genre, so saying one back would be inventing an opinion and appearing to have used it.
        Assert.Empty(labels);
    }

    [Fact]
    public void Three_dislikes_sharing_a_theme_name_it()
    {
        var labels = TasteAvoidanceService.Select(
            [Facet("Harem", NarrowDf, 1, 2, 3)], Titles(3), Corpus);

        var label = Assert.Single(labels);
        Assert.Equal("Harem", label.Label);
        Assert.Equal("tag", label.Kind);
        Assert.Equal(3, label.Support);
        Assert.Equal(1.0, label.Share);
        Assert.Equal(["Avoided 1", "Avoided 2", "Avoided 3"], label.Examples.Select(x => x.Title));
    }

    [Fact]
    public void Three_dislikes_sharing_only_a_backdrop_name_nothing()
    {
        var labels = TasteAvoidanceService.Select(
            [Facet("School", BroadDf, 1, 2, 3)], Titles(3), Corpus);

        // The specificity floor is the cut that stops this surface printing "School" and "Japan",
        // which is the exact label problem the group mining doc describes.
        Assert.Empty(labels);
    }

    [Fact]
    public void A_theme_on_a_minority_of_the_avoided_set_names_nothing()
    {
        var labels = TasteAvoidanceService.Select(
            [Facet("Harem", NarrowDf, 1, 2, 3)], Titles(10), Corpus);

        // Three of ten is a corner of the set, not a description of it.
        Assert.Empty(labels);
    }

    [Fact]
    public void Support_ranks_above_specificity()
    {
        var labels = TasteAvoidanceService.Select(
            [
                Facet("Rare", 200, 1, 2, 3),
                Facet("Common enough", NarrowDf, 1, 2, 3, 4),
            ],
            Titles(4),
            Corpus);

        Assert.Equal(["Common enough", "Rare"], labels.Select(x => x.Label));
    }

    [Fact]
    public void Nothing_avoided_names_nothing()
    {
        Assert.Empty(TasteAvoidanceService.Select(
            [Facet("Harem", NarrowDf, 1, 2, 3)], Titles(0), Corpus));
    }

    [Fact]
    public void A_theme_the_readers_own_shelf_is_full_of_names_nothing()
    {
        // Four of five pushed down and 63 of 90 on the shelf. The reader reads this; four of them
        // disappointing says something about those four, not about the theme.
        var labels = TasteAvoidanceService.Select(
            [Facet("Romance", NarrowDf, 1, 2, 3, 4) with { PositiveSupport = 63 }],
            Titles(5),
            Corpus,
            shelfCount: 90);

        Assert.Empty(labels);
    }

    [Fact]
    public void A_theme_the_shelf_barely_carries_survives_and_carries_both_counts()
    {
        var labels = TasteAvoidanceService.Select(
            [Facet("Harem", NarrowDf, 1, 2, 3, 4) with { PositiveSupport = 2 }],
            Titles(5),
            Corpus,
            shelfCount: 90);

        var label = Assert.Single(labels);
        Assert.Equal(4, label.Support);
        Assert.Equal(0.8, label.Share, 6);
        Assert.Equal(2, label.PositiveSupport);
        Assert.Equal(2.0 / 90.0, label.PositiveShare, 6);
        Assert.Equal(90, label.ShelfCount);
    }

    [Fact]
    public void A_shelf_of_nothing_leaves_the_contrast_out_of_the_way()
    {
        // No shelf profile to read, so the rule cannot fire and the other three cuts decide alone.
        var labels = TasteAvoidanceService.Select(
            [Facet("Harem", NarrowDf, 1, 2, 3)], Titles(3), Corpus, shelfCount: 0);

        Assert.Single(labels);
    }
}
