using Maki.Core.Entities;
using Maki.Core.Recommendations;
using Maki.Core.Scrobbling;

namespace Maki.Core.Tests;

/// <summary>
/// What a watched anime is allowed to say about a manga, and which of an anime's relations counts
/// as the manga it came from.
/// </summary>
public class AnimeSignalPolicyTests
{
    [Theory]
    [InlineData(AnimeWatchStatus.Completed, 9, AnimeSignalRole.Positive)]
    [InlineData(AnimeWatchStatus.Completed, 7, AnimeSignalRole.Positive)]
    [InlineData(AnimeWatchStatus.Watching, 8, AnimeSignalRole.Positive)]
    [InlineData(AnimeWatchStatus.OnHold, 10, AnimeSignalRole.Positive)]
    [InlineData(AnimeWatchStatus.Completed, null, AnimeSignalRole.Positive)]
    [InlineData(AnimeWatchStatus.Watching, null, AnimeSignalRole.Neutral)]
    [InlineData(AnimeWatchStatus.Completed, 6, AnimeSignalRole.Neutral)]
    [InlineData(AnimeWatchStatus.Completed, 5, AnimeSignalRole.Neutral)]
    [InlineData(AnimeWatchStatus.Completed, 4, AnimeSignalRole.Avoided)]
    [InlineData(AnimeWatchStatus.Completed, 1, AnimeSignalRole.Avoided)]
    [InlineData(AnimeWatchStatus.Dropped, null, AnimeSignalRole.Avoided)]
    [InlineData(AnimeWatchStatus.Dropped, 8, AnimeSignalRole.Avoided)]
    public void Status_and_score_decide_the_role(AnimeWatchStatus status, int? score, AnimeSignalRole expected) =>
        Assert.Equal(expected, AnimeSignalPolicy.RoleOf(status, score));

    /// <summary>
    /// A plan-to-watch entry is not evidence in either direction, whatever somebody typed into its
    /// score box before watching it.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(2)]
    [InlineData(10)]
    public void Planning_is_always_ignored(int? score)
    {
        Assert.Equal(AnimeSignalRole.Neutral, AnimeSignalPolicy.RoleOf(AnimeWatchStatus.Planning, score));
        Assert.Equal(0, AnimeSignalPolicy.AvoidStrengthOf(AnimeWatchStatus.Planning, score));
        Assert.Equal(0, AnimeSignalPolicy.SeedWeightOf(AnimeWatchStatus.Planning, score));
    }

    [Fact]
    public void A_positive_seed_stays_under_the_neutral_weight_an_unrated_shelf_title_gets()
    {
        Assert.Equal(0.7, AnimeSignalPolicy.SeedWeightOf(AnimeWatchStatus.Completed, 10), 8);
        Assert.Equal(0.49, AnimeSignalPolicy.SeedWeightOf(AnimeWatchStatus.Completed, 7), 8);
        Assert.Equal(0.56, AnimeSignalPolicy.SeedWeightOf(AnimeWatchStatus.Completed, null), 8);
        Assert.True(AnimeSignalPolicy.SeedWeightOf(AnimeWatchStatus.Completed, 10) < 1.0);
    }

    /// <summary>
    /// The curve is over 0-10, not over the 1-5 stars a library row carries. 4/10 is the last score
    /// that complains and 5/10 is silent; nothing here may be read as a star rating.
    /// </summary>
    [Theory]
    [InlineData(1, 1.0)]
    [InlineData(2, 0.75)]
    [InlineData(3, 0.5)]
    [InlineData(4, 0.25)]
    [InlineData(5, 0)]
    [InlineData(6, 0)]
    [InlineData(10, 0)]
    public void The_avoid_curve_runs_over_the_ten_point_scale(int score, double expected)
    {
        Assert.Equal(expected, AnimeSignalPolicy.AvoidStrengthOfScore(score), 8);
        Assert.Equal(expected, AnimeSignalPolicy.AvoidStrengthOf(AnimeWatchStatus.Completed, score), 8);
    }

    /// <summary>
    /// Dropping is a weaker complaint than a thumbs down, and a drop that also carries a 1/10 is the
    /// stronger of the two rather than the one the reader happened to record last.
    /// </summary>
    [Fact]
    public void A_drop_pushes_below_a_thumbs_down_but_never_below_its_own_score()
    {
        Assert.Equal(0.75, AnimeSignalPolicy.AvoidStrengthOf(AnimeWatchStatus.Dropped, null), 8);
        Assert.Equal(1.0, AnimeSignalPolicy.AvoidStrengthOf(AnimeWatchStatus.Dropped, 1), 8);
        Assert.Equal(0.75, AnimeSignalPolicy.AvoidStrengthOf(AnimeWatchStatus.Dropped, 4), 8);
    }

    /// <summary>
    /// The light-novel case, which is why format outranks relation type. AniList files a light novel
    /// under type MANGA, so a show sourced from one has its LN on the SOURCE edge and its manga on
    /// the ADAPTATION edge. Picking the novel would then lose the match twice over: the catalogue
    /// drops novels, and the manga that was right there never got looked at.
    /// </summary>
    [Fact]
    public void A_manga_adaptation_beats_the_light_novel_that_sourced_it()
    {
        var edges = new[]
        {
            new AnimeMangaRelation("SOURCE", 20, 200, "MANGA", "NOVEL"),
            new AnimeMangaRelation("ADAPTATION", 10, 100, "MANGA", "MANGA"),
        };
        Assert.Equal(10, AnimeRelationPicker.Pick(edges)!.Value.Id);
        // Nothing but the novel is no match at all, rather than a novel the catalogue will refuse.
        Assert.Null(AnimeRelationPicker.Pick(edges.Where(e => e.Id == 20)));
    }

    [Fact]
    public void Among_manga_edges_the_source_still_beats_an_adaptation()
    {
        var edges = new[]
        {
            new AnimeMangaRelation("ADAPTATION", 10, 100, "MANGA", "MANGA"),
            new AnimeMangaRelation("SOURCE", 30, 300, "MANGA", "MANGA"),
        };
        Assert.Equal(30, AnimeRelationPicker.Pick(edges)!.Value.Id);
    }

    [Fact]
    public void Anime_relations_and_unranked_relation_types_are_never_picked()
    {
        Assert.Null(AnimeRelationPicker.Pick([new AnimeMangaRelation("SOURCE", 1, 2, "ANIME", "TV")]));
        Assert.Null(AnimeRelationPicker.Pick([new AnimeMangaRelation("SEQUEL", 1, 2, "MANGA", "MANGA")]));
        Assert.Null(AnimeRelationPicker.Pick([]));
    }

    [Fact]
    public void Parent_and_alternative_are_last_resorts_in_that_order()
    {
        var edges = new[]
        {
            new AnimeMangaRelation("ALTERNATIVE", 1, null, "MANGA", "MANGA"),
            new AnimeMangaRelation("PARENT", 2, null, "MANGA", "MANGA"),
        };
        Assert.Equal(2, AnimeRelationPicker.Pick(edges)!.Value.Id);
    }

    [Fact]
    public void Mal_prefers_an_adaptation_over_a_parent_story()
    {
        Assert.True(AnimeRelationPicker.MalRelationRank("adaptation") <
                    AnimeRelationPicker.MalRelationRank("parent_story"));
        Assert.True(AnimeRelationPicker.MalRelationRank("parent_story") <
                    AnimeRelationPicker.MalRelationRank("full_story"));
    }

    /// <summary>
    /// A spin-off is not the work the anime came from. Ranking those last rather than excluding them
    /// would seed a gag 4-koma off somebody's favourite show.
    /// </summary>
    [Theory]
    [InlineData("spin_off")]
    [InlineData("side_story")]
    [InlineData("character")]
    [InlineData("summary")]
    [InlineData("other")]
    [InlineData("alternative_version")]
    [InlineData(null)]
    public void Mal_refuses_every_relation_that_is_not_the_source_work(string? relationType) =>
        Assert.Equal(int.MaxValue, AnimeRelationPicker.MalRelationRank(relationType));
}
