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
        foreach (var level in Enum.GetValues<AnimeSignalStrength>())
        {
            Assert.Equal(0, AnimeSignalPolicy.AvoidStrengthOf(AnimeWatchStatus.Planning, score, level));
            Assert.Equal(0, AnimeSignalPolicy.SeedWeightOf(AnimeWatchStatus.Planning, score, level));
        }
    }

    [Fact]
    public void A_subtle_seed_stays_under_the_neutral_weight_an_unrated_shelf_title_gets()
    {
        const AnimeSignalStrength subtle = AnimeSignalStrength.Subtle;
        Assert.Equal(0.7, AnimeSignalPolicy.SeedWeightOf(AnimeWatchStatus.Completed, 10, subtle), 8);
        Assert.Equal(0.49, AnimeSignalPolicy.SeedWeightOf(AnimeWatchStatus.Completed, 7, subtle), 8);
        Assert.Equal(0.56, AnimeSignalPolicy.SeedWeightOf(AnimeWatchStatus.Completed, null, subtle), 8);
        Assert.True(AnimeSignalPolicy.SeedWeightOf(AnimeWatchStatus.Completed, 10, subtle) < 1.0);
    }

    /// <summary>
    /// The three levels are one number apart, and that number is "how much of a manga rating does
    /// this carry". The library seeds a rated series at <c>rating / 5.0</c>, so a full share has to
    /// reproduce it exactly or the level is lying about what it means.
    /// </summary>
    [Theory]
    [InlineData(AnimeSignalStrength.Subtle, 0.35)]
    [InlineData(AnimeSignalStrength.Balanced, 0.5)]
    [InlineData(AnimeSignalStrength.Full, 1.0)]
    public void A_level_is_a_share_of_what_rating_the_manga_would_carry(
        AnimeSignalStrength level, double share)
    {
        Assert.Equal(share, AnimeSignalPolicy.RatingShareOf(level), 8);
        foreach (var score in new[] { 7, 8, 9, 10 })
        {
            // What the library would give the same number, from SeedWeightService.Weigh.
            var asRating = score / 5.0;
            Assert.Equal(asRating * share,
                AnimeSignalPolicy.SeedWeightOf(AnimeWatchStatus.Completed, score, level), 8);
        }
    }

    /// <summary>
    /// The default is the one that matters, since nobody has to touch the dial. A loved anime lands
    /// exactly on the neutral weight: it contributes its genres and tags to the profile and cannot
    /// out-vote a book the reader rated.
    /// </summary>
    [Fact]
    public void The_default_puts_a_loved_anime_exactly_at_neutral()
    {
        Assert.Equal(AnimeSignalStrength.Balanced, AnimeSignalPolicy.DefaultStrength);
        Assert.Equal(
            1.0,
            AnimeSignalPolicy.SeedWeightOf(
                AnimeWatchStatus.Completed, 10, AnimeSignalPolicy.DefaultStrength),
            8);
    }

    /// <summary>
    /// The reason the dial exists. A low anime score is the least trustworthy thing a watch history
    /// says about a book - people rate an adaptation for the adaptation - so a complaint must never
    /// push harder than the same level's praise pulls. Before the level, a 1/10 anime avoided at the
    /// full 1.0 a deliberate thumbs down carries while a 10/10 seeded at 0.7.
    /// </summary>
    [Theory]
    [InlineData(AnimeSignalStrength.Subtle, 0.35)]
    [InlineData(AnimeSignalStrength.Balanced, 0.5)]
    [InlineData(AnimeSignalStrength.Full, 1.0)]
    public void A_complaint_is_discounted_by_the_same_share_as_a_recommendation(
        AnimeSignalStrength level, double share)
    {
        // A thumbs down is 1.0, so a 1/10 anime is exactly `share` of one.
        Assert.Equal(share, AnimeSignalPolicy.AvoidStrengthOf(AnimeWatchStatus.Completed, 1, level), 8);
        Assert.Equal(AnimeSignalPolicy.DroppedStrength * share,
            AnimeSignalPolicy.AvoidStrengthOf(AnimeWatchStatus.Dropped, null, level), 8);
        Assert.True(AnimeSignalPolicy.AvoidStrengthOf(AnimeWatchStatus.Completed, 1, level) <= 1.0);
    }

    /// <summary>
    /// The level scales how hard a signal pushes, never which way it points. A panel whose badges
    /// flipped as somebody moved the dial would be describing the dial, not their watch history.
    /// </summary>
    [Theory]
    [InlineData(AnimeWatchStatus.Completed, 9, AnimeSignalRole.Positive)]
    [InlineData(AnimeWatchStatus.Completed, 2, AnimeSignalRole.Avoided)]
    [InlineData(AnimeWatchStatus.Completed, 6, AnimeSignalRole.Neutral)]
    [InlineData(AnimeWatchStatus.Dropped, null, AnimeSignalRole.Avoided)]
    public void The_level_never_changes_which_way_a_signal_points(
        AnimeWatchStatus status, int? score, AnimeSignalRole expected)
    {
        Assert.Equal(expected, AnimeSignalPolicy.RoleOf(status, score));

        // Every level agrees on which channel it lands in, only on how loud it is there.
        foreach (var level in Enum.GetValues<AnimeSignalStrength>())
        {
            Assert.Equal(expected == AnimeSignalRole.Avoided,
                AnimeSignalPolicy.AvoidStrengthOf(status, score, level) > 0);
            Assert.Equal(expected == AnimeSignalRole.Positive,
                AnimeSignalPolicy.SeedWeightOf(status, score, level) > 0);
        }
    }

    /// <summary>
    /// The rule that stops one series counting twice, and the order it reports reasons in. A title
    /// can easily be on the shelf and thumbed and excluded at once; the reason shown has to be a
    /// stable one rather than whichever set happened to be checked first.
    /// </summary>
    [Fact]
    public void A_readers_own_evidence_replaces_their_watch_history()
    {
        var precedence = new AnimeSignalPrecedence(
            Library: new HashSet<long> { 1, 9 },
            Opinion: new HashSet<long> { 2, 9 },
            Ignored: new HashSet<long> { 3, 9 });

        Assert.Equal(AnimeSupersededBy.Library, precedence.Reason(1));
        Assert.Equal(AnimeSupersededBy.Feedback, precedence.Reason(2));
        Assert.Equal(AnimeSupersededBy.Ignored, precedence.Reason(3));
        // On the shelf and thumbed and excluded: the shelf is the one to say, since it is the
        // reader's own reading rather than a note they left about recommendations.
        Assert.Equal(AnimeSupersededBy.Library, precedence.Reason(9));

        Assert.All(new long[] { 1, 2, 3, 9 }, id => Assert.True(precedence.Supersedes(id)));
        Assert.Equal(AnimeSupersededBy.None, precedence.Reason(4));
        Assert.False(precedence.Supersedes(4));
    }

    [Theory]
    [InlineData("subtle", AnimeSignalStrength.Subtle)]
    [InlineData("Balanced", AnimeSignalStrength.Balanced)]
    [InlineData("FULL", AnimeSignalStrength.Full)]
    // An unset setting, and anything written by a client that guessed at the name.
    [InlineData(null, AnimeSignalStrength.Balanced)]
    [InlineData("", AnimeSignalStrength.Balanced)]
    [InlineData("medium", AnimeSignalStrength.Balanced)]
    public void An_unrecognised_stored_level_falls_back_to_the_default(
        string? raw, AnimeSignalStrength expected)
    {
        Assert.Equal(expected, AnimeSignalPolicy.ParseStrength(raw));
        Assert.Equal(expected, AnimeSignalPolicy.ParseStrength(AnimeSignalPolicy.NameOf(expected)));
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
        Assert.Equal(expected,
            AnimeSignalPolicy.AvoidStrengthOf(AnimeWatchStatus.Completed, score, AnimeSignalStrength.Full), 8);
    }

    /// <summary>
    /// Dropping is a weaker complaint than a thumbs down, and a drop that also carries a 1/10 is the
    /// stronger of the two rather than the one the reader happened to record last.
    /// </summary>
    [Fact]
    public void A_drop_pushes_below_a_thumbs_down_but_never_below_its_own_score()
    {
        const AnimeSignalStrength full = AnimeSignalStrength.Full;
        Assert.Equal(0.75, AnimeSignalPolicy.AvoidStrengthOf(AnimeWatchStatus.Dropped, null, full), 8);
        Assert.Equal(1.0, AnimeSignalPolicy.AvoidStrengthOf(AnimeWatchStatus.Dropped, 1, full), 8);
        Assert.Equal(0.75, AnimeSignalPolicy.AvoidStrengthOf(AnimeWatchStatus.Dropped, 4, full), 8);
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
}
