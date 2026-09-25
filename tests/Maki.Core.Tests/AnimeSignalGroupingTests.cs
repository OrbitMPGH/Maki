using Maki.Core.Entities;
using Maki.Core.Recommendations;

namespace Maki.Core.Tests;

/// <summary>
/// What happens to the two ways a reader's anime list says the same thing twice: a show listed on
/// both their trackers, and a franchise whose seasons are separate anime over one manga.
/// </summary>
public class AnimeSignalGroupingTests
{
    private static AnimeSignalRow Row(
        string service, long animeId, AnimeWatchStatus status, int? score,
        long? malAnimeId = null, long? mangaBakaId = null, string? title = null) =>
        new(service, animeId, malAnimeId, title ?? $"Anime {animeId}", score, status, mangaBakaId);

    /// <summary>
    /// The Jellyfin case: one watch history scrobbled to two trackers. Both rows describe the same
    /// viewing, so the result is one entry carrying both badges, not two entries agreeing.
    /// </summary>
    [Fact]
    public void The_same_show_on_two_trackers_collapses_to_one_entry()
    {
        var groups = AnimeSignalGrouping.Group(
        [
            Row("anilist", 101, AnimeWatchStatus.Completed, 9, malAnimeId: 55, mangaBakaId: 7),
            Row("mal", 55, AnimeWatchStatus.Completed, 9, malAnimeId: 55, mangaBakaId: 7),
        ]);

        var group = Assert.Single(groups);
        Assert.Equal(9, group.Score);
        Assert.Equal(1, group.AnimeCount);
        Assert.Equal(["anilist", "mal"], group.Services);
    }

    /// <summary>
    /// Two trackers holding two different scores is one opinion recorded twice, not two opinions,
    /// so they average rather than both landing in the franchise average as separate seasons.
    /// </summary>
    [Fact]
    public void Two_trackers_disagreeing_about_a_score_average_before_anything_else()
    {
        var groups = AnimeSignalGrouping.Group(
        [
            Row("anilist", 101, AnimeWatchStatus.Completed, 10, malAnimeId: 55, mangaBakaId: 7),
            Row("mal", 55, AnimeWatchStatus.Completed, 8, malAnimeId: 55, mangaBakaId: 7),
        ]);

        Assert.Equal(9, Assert.Single(groups).Score);
    }

    /// <summary>
    /// The ordering that makes duplicates harmless. Dedupe first means a show listed twice weighs
    /// the same as a show listed once: here season 1 (9, on both trackers) and season 2 (5, on one)
    /// come to 7, not the 7.67 that counting the duplicate would give.
    /// </summary>
    [Fact]
    public void Duplicates_do_not_get_a_vote_in_the_season_average()
    {
        var groups = AnimeSignalGrouping.Group(
        [
            Row("anilist", 101, AnimeWatchStatus.Completed, 9, malAnimeId: 55, mangaBakaId: 7),
            Row("mal", 55, AnimeWatchStatus.Completed, 9, malAnimeId: 55, mangaBakaId: 7),
            Row("anilist", 102, AnimeWatchStatus.Completed, 5, malAnimeId: 56, mangaBakaId: 7),
        ]);

        var group = Assert.Single(groups);
        Assert.Equal(7, group.Score);
        Assert.Equal(2, group.AnimeCount);
    }

    /// <summary>
    /// Seasons of one show are one opinion about one manga, and a reader whose enthusiasm faded
    /// across four of them means the average, not the season they liked best.
    /// </summary>
    [Fact]
    public void Seasons_over_one_manga_average_into_a_single_signal()
    {
        var groups = AnimeSignalGrouping.Group(
        [
            Row("anilist", 1, AnimeWatchStatus.Completed, 10, mangaBakaId: 7, title: "Vinland Saga"),
            Row("anilist", 2, AnimeWatchStatus.Completed, 6, mangaBakaId: 7, title: "Vinland Saga Season 2"),
        ]);

        var group = Assert.Single(groups);
        Assert.Equal(8, group.Score);
        Assert.Equal(2, group.AnimeCount);
        // The franchise as the reader would name it, not whichever season sorted first.
        Assert.Equal("Vinland Saga", group.Title);
        Assert.Equal(AnimeSignalRole.Positive, group.Role);
    }

    /// <summary>
    /// The averaging has to be able to move a signal across a threshold or it is decoration. Loving
    /// one season and disliking the next is a lukewarm opinion, and a lukewarm opinion seeds nothing.
    /// </summary>
    [Fact]
    public void A_franchise_that_fell_off_lands_between_the_two_verdicts()
    {
        var groups = AnimeSignalGrouping.Group(
        [
            Row("anilist", 1, AnimeWatchStatus.Completed, 9, mangaBakaId: 7),
            Row("anilist", 2, AnimeWatchStatus.Completed, 4, mangaBakaId: 7),
        ]);

        var group = Assert.Single(groups);
        Assert.Equal(6.5, group.Score);
        // Neither a seed nor a complaint: 9 alone would have seeded, 4 alone would have avoided.
        Assert.Equal(AnimeSignalRole.Neutral, group.Role);
        Assert.Equal(0, AnimeSignalPolicy.SeedWeightOf(
            group.Status, group.Score, AnimeSignalPolicy.DefaultStrength));
        Assert.Equal(0, AnimeSignalPolicy.AvoidStrengthOf(
            group.Status, group.Score, AnimeSignalPolicy.DefaultStrength));
    }

    /// <summary>
    /// Dropping the fourth season of something you sat through three of is not a complaint about
    /// the manga, so completing anything has to outrank dropping anything else.
    /// </summary>
    [Fact]
    public void Finishing_one_season_outranks_dropping_another()
    {
        var groups = AnimeSignalGrouping.Group(
        [
            Row("anilist", 1, AnimeWatchStatus.Completed, 9, mangaBakaId: 7),
            Row("anilist", 2, AnimeWatchStatus.Dropped, 6, mangaBakaId: 7),
        ]);

        var group = Assert.Single(groups);
        Assert.Equal(AnimeWatchStatus.Completed, group.Status);
        Assert.Equal(AnimeSignalRole.Positive, group.Role);
    }

    /// <summary>A franchise nothing was ever finished of keeps the drop, which is the whole point of it.</summary>
    [Fact]
    public void A_franchise_only_ever_dropped_stays_a_complaint()
    {
        var groups = AnimeSignalGrouping.Group(
        [
            Row("anilist", 1, AnimeWatchStatus.Dropped, 6, mangaBakaId: 7),
            Row("anilist", 2, AnimeWatchStatus.Planning, 9, mangaBakaId: 7),
        ]);

        var group = Assert.Single(groups);
        Assert.Equal(AnimeWatchStatus.Dropped, group.Status);
        Assert.Equal(AnimeSignalPolicy.DroppedStrength,
            AnimeSignalPolicy.AvoidStrengthOf(group.Status, group.Score, AnimeSignalStrength.Full), 8);
    }

    /// <summary>
    /// A score typed into an unwatched season's box is somebody rating a premise, and the policy
    /// already refuses to read it on its own. It must not reach the average through the side door.
    /// </summary>
    [Fact]
    public void A_plan_to_watch_season_stays_out_of_the_average()
    {
        var groups = AnimeSignalGrouping.Group(
        [
            Row("anilist", 1, AnimeWatchStatus.Completed, 8, mangaBakaId: 7),
            Row("anilist", 2, AnimeWatchStatus.Planning, 2, mangaBakaId: 7),
        ]);

        Assert.Equal(8, Assert.Single(groups).Score);
    }

    /// <summary>
    /// Unscored seasons are silence, not zero. Averaging them in as 0 would turn a franchise
    /// somebody rated 8 into a 4 and push it out of the seeds entirely.
    /// </summary>
    [Fact]
    public void Unscored_seasons_do_not_drag_the_average_down()
    {
        var groups = AnimeSignalGrouping.Group(
        [
            Row("anilist", 1, AnimeWatchStatus.Completed, 8, mangaBakaId: 7),
            Row("anilist", 2, AnimeWatchStatus.Completed, null, mangaBakaId: 7),
        ]);

        Assert.Equal(8, Assert.Single(groups).Score);
    }

    /// <summary>
    /// Only rows that resolved to a manga may merge. Two unmatched shows have nothing in common but
    /// the fact that nothing was known about either, which is not a reason to call them one work.
    /// </summary>
    [Fact]
    public void Unmatched_entries_stay_separate()
    {
        var groups = AnimeSignalGrouping.Group(
        [
            Row("anilist", 1, AnimeWatchStatus.Completed, 8),
            Row("anilist", 2, AnimeWatchStatus.Completed, 3),
        ]);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.Null(g.MangaBakaId));
        Assert.Equal(2, groups.Select(g => g.Key).Distinct().Count());
    }

    /// <summary>
    /// An AniList row with no MyAnimeList cross-reference cannot be told apart from its twin, so it
    /// stands alone rather than being merged onto a guess. Titles are not a fallback: the two
    /// trackers serve different ones.
    /// </summary>
    [Fact]
    public void Without_a_shared_id_an_unmatched_row_is_its_own_entry()
    {
        var groups = AnimeSignalGrouping.Group(
        [
            Row("anilist", 101, AnimeWatchStatus.Completed, 9, title: "Frieren"),
            Row("mal", 55, AnimeWatchStatus.Completed, 9, malAnimeId: 55, title: "Sousou no Frieren"),
        ]);

        Assert.Equal(2, groups.Count);
    }

    /// <summary>
    /// The panel's row key has to survive a re-sync, and the ordering SQLite returns rows in is not
    /// something a stored key may depend on.
    /// </summary>
    [Fact]
    public void Grouping_does_not_depend_on_row_order()
    {
        AnimeSignalRow[] rows =
        [
            Row("mal", 55, AnimeWatchStatus.Completed, 8, malAnimeId: 55, mangaBakaId: 7, title: "B"),
            Row("anilist", 101, AnimeWatchStatus.Completed, 9, malAnimeId: 56, mangaBakaId: 7, title: "A"),
            Row("anilist", 102, AnimeWatchStatus.Dropped, 3, mangaBakaId: 9, title: "C"),
        ];

        var forwards = AnimeSignalGrouping.Group(rows);
        var backwards = AnimeSignalGrouping.Group(rows.Reverse());

        Assert.Equal(
            forwards.OrderBy(g => g.Key).Select(g => (g.Key, g.Title, g.Score, g.Status)),
            backwards.OrderBy(g => g.Key).Select(g => (g.Key, g.Title, g.Score, g.Status)));
    }

    /// <summary>
    /// The sync stores unscored Completed and Watching rows for the anime resume callout. They
    /// carry no opinion, so the recommender must not see them, legacy unscored rows included.
    /// </summary>
    [Fact]
    public void Group_drops_unscored_rows()
    {
        var groups = AnimeSignalGrouping.Group(
        [
            Row("anilist", 1, AnimeWatchStatus.Completed, null, mangaBakaId: 7),
            Row("anilist", 2, AnimeWatchStatus.Dropped, null, mangaBakaId: 8),
            Row("anilist", 3, AnimeWatchStatus.Completed, 9, mangaBakaId: 9),
        ]);

        var group = Assert.Single(groups);
        Assert.Equal(9, group.MangaBakaId);
        Assert.Equal(1, group.AnimeCount);
    }

    [Fact]
    public void Watched_keeps_seasons_apart_and_keeps_unscored_rows()
    {
        var seasons = AnimeSignalGrouping.Watched(
        [
            Row("anilist", 1, AnimeWatchStatus.Completed, null, mangaBakaId: 7, title: "Vinland Saga"),
            Row("anilist", 2, AnimeWatchStatus.Watching, 8, mangaBakaId: 7, title: "Vinland Saga Season 2"),
        ]);

        Assert.Equal(2, seasons.Count);
        Assert.Equal([1L, 2L], seasons.Select(s => s.AnimeId).Order());
        Assert.All(seasons, s => Assert.Equal(7, s.MangaBakaId));
        Assert.Null(seasons.Single(s => s.AnimeId == 1).Score);
    }

    /// <summary>
    /// One show on two trackers is one season. Each field comes from whichever tracker knows it,
    /// progress is the furthest either recorded, and the id is AniList's when it is there.
    /// </summary>
    [Fact]
    public void Watched_dedupes_trackers_preferring_known_fields_and_max_progress()
    {
        var seasons = AnimeSignalGrouping.Watched(
        [
            new AnimeSignalRow("mal", 55, 55, "Shingeki no Kyojin", 8, AnimeWatchStatus.Watching, 7,
                Format: "TV", StartDate: new DateOnly(2013, 4, 7), EndDate: null, Episodes: 25, Progress: 20),
            new AnimeSignalRow("anilist", 101, 55, "Attack on Titan", 9, AnimeWatchStatus.Completed, null,
                Format: null, StartDate: null, EndDate: new DateOnly(2013, 9, 29), Episodes: null, Progress: 12),
        ]);

        var season = Assert.Single(seasons);
        Assert.Equal(101, season.AnimeId);
        Assert.Equal(55, season.MalAnimeId);
        Assert.Equal("Attack on Titan", season.Title);
        Assert.Equal(["anilist", "mal"], season.Services);
        Assert.Equal(9, season.Score);
        Assert.Equal(AnimeWatchStatus.Completed, season.Status);
        Assert.Equal("TV", season.Format);
        Assert.Equal(new DateOnly(2013, 4, 7), season.StartDate);
        Assert.Equal(new DateOnly(2013, 9, 29), season.EndDate);
        Assert.Equal(25, season.Episodes);
        Assert.Equal(20, season.Progress);
        Assert.Equal(7, season.MangaBakaId);
    }
}
