using Maki.Core.Entities;
using Maki.Core.Recommendations;

namespace Maki.Core.Tests;

/// <summary>
/// Where the reader's watched anime leaves off in the manga. Every doubtful case must come back
/// null or low: sending someone past chapters the anime never adapted is the failure that matters.
/// </summary>
public class AnimeResumeResolverTests
{
    private static long _nextId = 1;

    private static AnimeWatchedSeason Row(
        string title,
        AnimeWatchStatus status,
        string? format = "TV",
        string? start = null,
        string? end = null,
        int? episodes = 12,
        int? progress = null,
        int? score = null,
        string[]? services = null) =>
        new(
            _nextId++,
            null,
            title,
            services ?? ["anilist"],
            score,
            status,
            format,
            start is null ? null : DateOnly.Parse(start),
            end is null ? null : DateOnly.Parse(end),
            episodes,
            progress,
            7);

    private static AnimeSpan Season(string label, decimal from, decimal? to) =>
        new(label, from, to, to is null, AnimeSpanKind.Season);

    private static AnimeSpan Film(string label, decimal from, decimal? to) =>
        new(label, from, to, to is null, AnimeSpanKind.Film);

    private static readonly AnimeSpan[] TwoSeasons = [Season("S1", 1, 50), Season("S2", 51, 100)];

    private static AnimeWatchedSeason S1Done() =>
        Row("Show", AnimeWatchStatus.Completed, start: "2020-01-05", end: "2020-03-29", score: 8);

    [Fact]
    public void Caught_up_covers_to_the_end_of_the_last_season()
    {
        var result = AnimeResumeResolver.Resolve(
        [
            S1Done(),
            Row("Show Season 2", AnimeWatchStatus.Completed, start: "2021-01-10", end: "2021-03-28", score: 9),
        ], TwoSeasons, 120);

        Assert.NotNull(result);
        Assert.Equal(100m, result.CoveredTo);
        Assert.Equal("S2", result.CoveredLabel);
        Assert.Equal(AnimeResumeBasis.AllSeasons, result.Basis);
        Assert.Equal("Show Season 2", result.AnimeTitle);
        Assert.Equal(8.5, result.Score);
        Assert.Null(result.NextLabel);
        Assert.Null(result.NextFrom);
        Assert.Null(result.NextTo);
    }

    [Fact]
    public void Caught_up_works_off_parsed_coverage_text()
    {
        var spans = AnimeCoverage.Parse("Vol 1, Chap 1 (S1) / Vol 8, Chap 51 (S2)", "Vol 7, Chap 50 (S1) / Vol 14, Chap 100 (S2)");

        var result = AnimeResumeResolver.Resolve(
        [
            S1Done(),
            Row("Show Season 2", AnimeWatchStatus.Completed, start: "2021-01-10", end: "2021-03-28"),
        ], spans, null);

        Assert.Equal(100m, result?.CoveredTo);
    }

    [Fact]
    public void First_season_done_and_second_not_listed_stops_at_the_first()
    {
        var result = AnimeResumeResolver.Resolve([S1Done()], TwoSeasons, null);

        Assert.NotNull(result);
        Assert.Equal(50m, result.CoveredTo);
        Assert.Equal("S1", result.CoveredLabel);
        Assert.Equal(AnimeResumeBasis.SeasonCount, result.Basis);
        Assert.Equal("S2", result.NextLabel);
        Assert.Equal(51m, result.NextFrom);
        Assert.Equal(100m, result.NextTo);
    }

    [Fact]
    public void Second_season_half_watched_still_stops_at_the_first()
    {
        var result = AnimeResumeResolver.Resolve(
        [
            S1Done(),
            Row("Show Season 2", AnimeWatchStatus.Watching, start: "2021-01-10", end: "2021-03-28", episodes: 12, progress: 5),
        ], TwoSeasons, null);

        Assert.Equal(50m, result?.CoveredTo);
        Assert.Equal(AnimeResumeBasis.SeasonCount, result?.Basis);
    }

    [Fact]
    public void Watching_with_every_episode_seen_counts_as_finished()
    {
        var result = AnimeResumeResolver.Resolve(
        [
            S1Done(),
            Row("Show Season 2", AnimeWatchStatus.Watching, start: "2021-01-10", end: "2021-03-28", episodes: 12, progress: 12),
        ], TwoSeasons, null);

        Assert.Equal(100m, result?.CoveredTo);
        Assert.Equal(AnimeResumeBasis.AllSeasons, result?.Basis);
    }

    [Fact]
    public void Watching_without_a_known_episode_count_is_not_finished()
    {
        var result = AnimeResumeResolver.Resolve(
        [
            S1Done(),
            Row("Show Season 2", AnimeWatchStatus.Watching, start: "2021-01-10", episodes: null, progress: 30),
        ], TwoSeasons, null);

        Assert.Equal(50m, result?.CoveredTo);
    }

    [Fact]
    public void Split_cour_folds_into_one_season()
    {
        AnimeSpan[] spans = [Season("S1", 1, 50), Season("S2", 51, 100), Season("S3", 101, 150)];

        var result = AnimeResumeResolver.Resolve(
        [
            S1Done(),
            Row("Show Season 2", AnimeWatchStatus.Completed, start: "2021-01-10", end: "2021-03-28"),
            Row("Show Season 2 Part 2", AnimeWatchStatus.Completed, start: "2021-04-20", end: "2021-06-27"),
        ], spans, null);

        Assert.NotNull(result);
        Assert.Equal(100m, result.CoveredTo);
        Assert.Equal("S2", result.CoveredLabel);
        Assert.Equal("S3", result.NextLabel);
        Assert.Equal("Show Season 2 Part 2", result.AnimeTitle);
    }

    [Fact]
    public void Split_cour_with_the_second_half_unfinished_is_not_finished()
    {
        AnimeSpan[] spans = [Season("S1", 1, 50), Season("S2", 51, 100), Season("S3", 101, 150)];

        var result = AnimeResumeResolver.Resolve(
        [
            S1Done(),
            Row("Show Season 2", AnimeWatchStatus.Completed, start: "2021-01-10", end: "2021-03-28"),
            Row("Show Season 2 Part 2", AnimeWatchStatus.Watching, start: "2021-04-20", end: "2021-06-27", progress: 4),
        ], spans, null);

        Assert.Equal(50m, result?.CoveredTo);
        Assert.Equal("S2", result?.NextLabel);
    }

    [Fact]
    public void Remakes_with_only_one_watched_give_nothing()
    {
        AnimeSpan[] spans = [Season("Season 1", 1, 60), Season("Season 1 (2019)", 1, 108)];

        var result = AnimeResumeResolver.Resolve(
        [
            Row("Show (2003)", AnimeWatchStatus.Completed, start: "2003-10-04", end: "2004-10-02"),
            Row("Show (2019)", AnimeWatchStatus.Planning, start: "2019-04-05", end: "2020-03-27"),
        ], spans, null);

        Assert.Null(result);
    }

    [Fact]
    public void Remakes_with_both_watched_cover_the_furthest_one()
    {
        AnimeSpan[] spans = [Season("Season 1", 1, 60), Season("Season 1 (2019)", 1, 108)];

        var result = AnimeResumeResolver.Resolve(
        [
            Row("Show (2003)", AnimeWatchStatus.Completed, start: "2003-10-04", end: "2004-10-02"),
            Row("Show (2019)", AnimeWatchStatus.Completed, start: "2019-04-05", end: "2020-03-27"),
        ], spans, null);

        Assert.Equal(108m, result?.CoveredTo);
        Assert.Equal("Season 1 (2019)", result?.CoveredLabel);
        Assert.Equal(AnimeResumeBasis.AllSeasons, result?.Basis);
    }

    [Fact]
    public void Seasons_sharing_a_boundary_chapter_resolve_as_a_sequence()
    {
        var spans = AnimeCoverage.Parse(
            "Vol 1, Chap 1 (S1) / Vol 7, Chap 54 Page 5 (S2) / Vol 12, Chap 98 (S3)",
            "Vol 7, Chap 54 Page 4 (S1) / Vol 11, Chap 97 (S2) / Vol 15, Chap 127 (S3)");

        var result = AnimeResumeResolver.Resolve(
            [Row("Demon Slayer", AnimeWatchStatus.Completed, start: "2019-04-06", end: "2019-09-28")], spans, null);

        Assert.NotNull(result);
        Assert.Equal(54m, result.CoveredTo);
        Assert.Equal("S2", result.NextLabel);
        Assert.Equal(54m, result.NextFrom);
        Assert.Equal(97m, result.NextTo);
    }

    [Fact]
    public void Remakes_overlapping_by_several_chapters_are_still_remakes()
    {
        AnimeSpan[] spans = [Season("Season 1", 1, 60), Season("Season 1 (2019)", 55, 108)];

        var result = AnimeResumeResolver.Resolve(
        [
            Row("Show (2003)", AnimeWatchStatus.Completed, start: "2003-10-04", end: "2004-10-02"),
            Row("Show (2019)", AnimeWatchStatus.Planning, start: "2019-04-05", end: "2020-03-27"),
        ], spans, null);

        Assert.Null(result);
    }

    private static readonly AnimeSpan[] SeasonFilmSeason =
        [Season("S1", 1, 53), Film("Mugen Train Movie", 54, 66), Season("S2", 67, 97)];

    [Fact]
    public void Watched_film_right_after_the_season_extends_the_frontier()
    {
        var result = AnimeResumeResolver.Resolve(
        [
            Row("Show", AnimeWatchStatus.Completed, start: "2019-04-06", end: "2019-09-28"),
            Row("Show the Movie", AnimeWatchStatus.Completed, format: "MOVIE", start: "2020-10-16", episodes: 1),
            Row("Show Season 2", AnimeWatchStatus.Planning, start: "2021-12-05"),
        ], SeasonFilmSeason, null);

        Assert.NotNull(result);
        Assert.Equal(66m, result.CoveredTo);
        Assert.Equal("Mugen Train Movie", result.CoveredLabel);
        Assert.Equal("S2", result.NextLabel);
        Assert.Equal("Show", result.AnimeTitle);
    }

    [Fact]
    public void Film_not_watched_does_not_extend()
    {
        var result = AnimeResumeResolver.Resolve(
        [
            Row("Show", AnimeWatchStatus.Completed, start: "2019-04-06", end: "2019-09-28"),
            Row("Show the Movie", AnimeWatchStatus.Planning, format: "MOVIE", start: "2020-10-16", episodes: 1),
        ], SeasonFilmSeason, null);

        Assert.Equal(53m, result?.CoveredTo);
        Assert.Equal("S1", result?.CoveredLabel);
    }

    [Fact]
    public void Film_that_aired_after_the_next_season_does_not_extend()
    {
        var result = AnimeResumeResolver.Resolve(
        [
            Row("Show", AnimeWatchStatus.Completed, start: "2019-04-06", end: "2019-09-28"),
            Row("Show Season 2", AnimeWatchStatus.Planning, start: "2021-12-05"),
            Row("Some Other Movie", AnimeWatchStatus.Completed, format: "MOVIE", start: "2022-06-01", episodes: 1),
        ], SeasonFilmSeason, null);

        Assert.Equal(53m, result?.CoveredTo);
    }

    [Fact]
    public void Last_season_still_airing_gives_nothing_when_caught_up()
    {
        AnimeSpan[] spans = [Season("S1", 1, 50), Season("S2", 51, null)];

        var result = AnimeResumeResolver.Resolve(
        [
            S1Done(),
            Row("Show Season 2", AnimeWatchStatus.Completed, start: "2021-01-10", end: "2021-03-28"),
        ], spans, null);

        Assert.Null(result);
    }

    [Fact]
    public void Frontier_past_the_last_known_chapter_gives_nothing()
    {
        Assert.Null(AnimeResumeResolver.Resolve([S1Done()], TwoSeasons, 40));
        Assert.Equal(50m, AnimeResumeResolver.Resolve([S1Done()], TwoSeasons, 50)?.CoveredTo);
    }

    [Fact]
    public void Nothing_finished_gives_nothing()
    {
        var result = AnimeResumeResolver.Resolve(
        [
            Row("Show", AnimeWatchStatus.Watching, start: "2020-01-05", end: "2020-03-29", progress: 3),
            Row("Show Season 2", AnimeWatchStatus.Completed, start: "2021-01-10", end: "2021-03-28"),
        ], TwoSeasons, null);

        Assert.Null(result);
    }

    [Fact]
    public void Show_on_two_trackers_arrives_as_one_row_with_both_services()
    {
        var result = AnimeResumeResolver.Resolve(
        [
            Row("Show", AnimeWatchStatus.Completed, start: "2020-01-05", end: "2020-03-29", score: 8, services: ["anilist", "mal"]),
            Row("Show Season 2", AnimeWatchStatus.Completed, start: "2021-01-10", end: "2021-03-28", services: ["mal"]),
        ], TwoSeasons, null);

        Assert.NotNull(result);
        Assert.Equal(100m, result.CoveredTo);
        Assert.Equal(new[] { "anilist", "mal" }, result.Services);
        Assert.Equal(8.0, result.Score);
    }

    [Fact]
    public void Several_tv_rows_with_missing_dates_give_nothing()
    {
        var result = AnimeResumeResolver.Resolve(
        [
            Row("Show", AnimeWatchStatus.Completed),
            Row("Show Season 2", AnimeWatchStatus.Completed),
        ], TwoSeasons, null);

        Assert.Null(result);
    }

    [Fact]
    public void Several_rows_with_one_missing_its_format_give_nothing()
    {
        var result = AnimeResumeResolver.Resolve(
        [
            S1Done(),
            Row("Show Season 2", AnimeWatchStatus.Completed, format: null, start: "2021-01-10"),
        ], TwoSeasons, null);

        Assert.Null(result);
    }

    [Fact]
    public void Single_row_without_format_or_date_is_not_trusted_against_a_single_season()
    {
        AnimeSpan[] spans = [Season("S1", 1, 50)];

        Assert.Null(AnimeResumeResolver.Resolve([Row("Show", AnimeWatchStatus.Completed, format: null)], spans, null));
        Assert.Null(AnimeResumeResolver.Resolve([Row("Show", AnimeWatchStatus.Completed)], spans, null));
    }

    [Fact]
    public void Single_row_without_format_or_date_is_not_trusted_against_several_seasons()
    {
        Assert.Null(AnimeResumeResolver.Resolve([Row("Show", AnimeWatchStatus.Completed, format: null)], TwoSeasons, null));
    }

    [Fact]
    public void Other_kind_spans_stand_in_when_no_season_is_labelled()
    {
        AnimeSpan[] spans = [new("Naruto", 1, 238, false, AnimeSpanKind.Other)];

        var result = AnimeResumeResolver.Resolve([S1Done()], spans, null);

        Assert.Equal(238m, result?.CoveredTo);
    }

    [Fact]
    public void Films_alone_are_not_seasons()
    {
        Assert.Null(AnimeResumeResolver.Resolve([S1Done()], [Film("Movie", 1, 20)], null));
    }

    private const string TitanStart =
        "Vol 1, Chap 1 (S1) / Vol 9, Chap 35 (S2) / Vol 13, Chap 51 (S3P1) / Vol 23, Chap 91 (S4P1)";
    private const string TitanEnd =
        "Vol 8, Chap 34 (S1 + OVA 1) / Vol 12, Chap 50 (S2) / Vol 22, Chap 90 (S3P2) / Vol 34, Chap 139 (S4P3)";

    private static AnimeWatchedSeason[] Titan(int finished) =>
        new[]
        {
            Row("Attack on Titan", AnimeWatchStatus.Completed, start: "2013-04-07", end: "2013-09-29"),
            Row("Attack on Titan Season 2", AnimeWatchStatus.Completed, start: "2017-04-01", end: "2017-06-17"),
            Row("Attack on Titan Season 3", AnimeWatchStatus.Completed, start: "2018-07-23", end: "2018-10-15"),
            Row("Attack on Titan Season 3 Part 2", AnimeWatchStatus.Completed, start: "2019-04-29", end: "2019-07-01"),
            Row("Attack on Titan Final Season", AnimeWatchStatus.Completed, start: "2020-12-07", end: "2021-03-29"),
            Row("Attack on Titan Final Season Part 2", AnimeWatchStatus.Completed, start: "2022-01-10", end: "2022-04-04"),
            Row("Attack on Titan Final Season Final Chapters", AnimeWatchStatus.Completed, start: "2023-03-04", end: "2023-11-05"),
        }.Take(finished).ToArray();

    [Fact]
    public void Titan_first_half_of_a_part_labelled_season_stops_before_it()
    {
        var result = AnimeResumeResolver.Resolve(Titan(3), AnimeCoverage.Parse(TitanStart, TitanEnd), null);

        Assert.NotNull(result);
        Assert.Equal(50m, result.CoveredTo);
        Assert.Equal("S2", result.CoveredLabel);
        Assert.Equal(AnimeResumeBasis.SeasonCount, result.Basis);
        Assert.Equal("Attack on Titan Season 2", result.AnimeTitle);
        Assert.Equal("S3P1", result.NextLabel);
        Assert.Equal(51m, result.NextFrom);
        Assert.Equal(90m, result.NextTo);
    }

    [Fact]
    public void Titan_both_halves_of_season_three_cover_it()
    {
        var result = AnimeResumeResolver.Resolve(Titan(4), AnimeCoverage.Parse(TitanStart, TitanEnd), null);

        Assert.NotNull(result);
        Assert.Equal(90m, result.CoveredTo);
        Assert.Equal("S3P1", result.CoveredLabel);
        Assert.Equal("Attack on Titan Season 3 Part 2", result.AnimeTitle);
        Assert.Equal("S4P1", result.NextLabel);
        Assert.Equal(91m, result.NextFrom);
        Assert.Equal(139m, result.NextTo);
    }

    [Fact]
    public void Titan_two_of_three_final_season_parts_still_stop_at_season_three()
    {
        var result = AnimeResumeResolver.Resolve(Titan(6), AnimeCoverage.Parse(TitanStart, TitanEnd), null);

        Assert.Equal(90m, result?.CoveredTo);
        Assert.Equal("S4P1", result?.NextLabel);
    }

    [Fact]
    public void Titan_every_part_is_caught_up()
    {
        var result = AnimeResumeResolver.Resolve(Titan(7), AnimeCoverage.Parse(TitanStart, TitanEnd), null);

        Assert.NotNull(result);
        Assert.Equal(139m, result.CoveredTo);
        Assert.Equal(AnimeResumeBasis.AllSeasons, result.Basis);
        Assert.Null(result.NextLabel);
    }

    [Fact]
    public void Titan_with_the_last_season_open_ended_is_never_caught_up()
    {
        var spans = AnimeCoverage.Parse(TitanStart, "Vol 8, Chap 34 (S1 + OVA 1) / Vol 12, Chap 50 (S2) / Vol 22, Chap 90 (S3P2)");

        Assert.Null(AnimeResumeResolver.Resolve(Titan(5), spans, null));
        Assert.Null(AnimeResumeResolver.Resolve(Titan(7), spans, null));
    }

    private static readonly IReadOnlyList<AnimeSpan> SpyFamily = AnimeCoverage.Parse(
        "Vol 1, Chap 1 (S1P1) / Vol 7, Chap 39 (S2) / Vol 9 Chap 60 (S3)",
        "Vol 7, Chap 38 (S1P2) / Vol 9, Chap 59 (S2) / Vol 13, Chap 87 (S3) Skips Chap 62.5, 68-68.5, 78");

    [Fact]
    public void Half_of_a_part_labelled_first_season_gives_nothing()
    {
        var result = AnimeResumeResolver.Resolve(
            [Row("SPY x FAMILY", AnimeWatchStatus.Completed, start: "2022-04-09", end: "2022-06-25")], SpyFamily, null);

        Assert.Null(result);
    }

    [Fact]
    public void Both_parts_of_a_part_labelled_first_season_cover_it()
    {
        var result = AnimeResumeResolver.Resolve(
        [
            Row("SPY x FAMILY", AnimeWatchStatus.Completed, start: "2022-04-09", end: "2022-06-25"),
            Row("SPY x FAMILY Part 2", AnimeWatchStatus.Completed, start: "2022-10-01", end: "2022-12-24"),
        ], SpyFamily, null);

        Assert.NotNull(result);
        Assert.Equal(38m, result.CoveredTo);
        Assert.Equal("S1P1", result.CoveredLabel);
        Assert.Equal("S2", result.NextLabel);
        Assert.Equal(39m, result.NextFrom);
        Assert.Equal(59m, result.NextTo);
    }

    [Fact]
    public void Episode_number_in_a_label_is_not_a_part()
    {
        var spans = AnimeCoverage.Parse(
            "Vol 1, Chap 1 (S1P1) / Vol 11, Chap 52 (S2P1 EP4) / Vol 25, Chap 118 (S3P1 EP3)",
            "Vol 10, Chap 51 (S1P2 + OVA) / Vol 25, Chap 117 (S2P2)");

        var result = AnimeResumeResolver.Resolve(
        [
            Row("Mushoku Tensei", AnimeWatchStatus.Completed, start: "2021-01-11", end: "2021-03-22"),
            Row("Mushoku Tensei Part 2", AnimeWatchStatus.Completed, start: "2021-10-04", end: "2021-12-20"),
            Row("Mushoku Tensei II", AnimeWatchStatus.Completed, start: "2023-07-03", end: "2023-09-25"),
            Row("Mushoku Tensei II Part 2", AnimeWatchStatus.Completed, start: "2024-04-08", end: "2024-06-24"),
        ], spans, null);

        Assert.NotNull(result);
        Assert.Equal(117m, result.CoveredTo);
        Assert.Equal("S3P1 EP3", result.NextLabel);
        Assert.Null(result.NextTo);
    }

    [Fact]
    public void Part_labelled_last_season_needs_every_part_for_caught_up()
    {
        var spans = AnimeCoverage.Parse(
            "Vol 1, Chap 1 (S1) / Vol 8, Chap 72 (S2) / Vol 17, Chap 150 (S3) / Vol 23, Chap 207 (S4P1) / Vol 33, Chap 293 (Dumpster Battle)",
            "Vol 8, Chap 71 (S1) / Vol 17, Chap 149 (S2) / Vol 23, Chap 206 (S3+OVA) / Vol 33, Chap 292 (S4P2) / Vol 37, Chap 325 (Dumpster Battle) Abridged");
        AnimeWatchedSeason[] rows =
        [
            Row("Haikyu!!", AnimeWatchStatus.Completed, start: "2014-04-06", end: "2014-09-21"),
            Row("Haikyu!! 2nd Season", AnimeWatchStatus.Completed, start: "2015-10-04", end: "2016-03-27"),
            Row("Haikyu!! 3rd Season", AnimeWatchStatus.Completed, start: "2016-10-08", end: "2016-12-10"),
            Row("Haikyu!! To the Top", AnimeWatchStatus.Completed, start: "2020-01-11", end: "2020-04-04"),
            Row("Haikyu!! To the Top Part 2", AnimeWatchStatus.Completed, start: "2020-10-03", end: "2020-12-19"),
        ];

        var partial = AnimeResumeResolver.Resolve(rows[..4], spans, null);
        var all = AnimeResumeResolver.Resolve(rows, spans, null);

        Assert.Equal(206m, partial?.CoveredTo);
        Assert.Equal("S4P1", partial?.NextLabel);
        Assert.Equal(292m, all?.CoveredTo);
        Assert.Equal(AnimeResumeBasis.AllSeasons, all?.Basis);
    }

    [Fact]
    public void Part_labels_from_different_seasons_do_not_expand()
    {
        AnimeSpan[] spans =
        [
            new("S1P1", 1, 50, false, AnimeSpanKind.Season, "S2P2"),
            Season("S3", 51, 100),
        ];

        var result = AnimeResumeResolver.Resolve([S1Done()], spans, null);

        Assert.Equal(50m, result?.CoveredTo);
        Assert.Equal("S3", result?.NextLabel);
    }

    [Fact]
    public void More_tv_entries_than_season_slots_gives_nothing()
    {
        var result = AnimeResumeResolver.Resolve(
        [
            S1Done(),
            Row("Show Recap", AnimeWatchStatus.Completed, start: "2020-07-05", end: "2020-07-12"),
            Row("Show Season 2", AnimeWatchStatus.Completed, start: "2021-01-10", end: "2021-03-28"),
        ], TwoSeasons, null);

        Assert.Null(result);
    }
}
