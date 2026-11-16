using Maki.Api.Dtos;
using Maki.Api.Services;

namespace Maki.Api.Tests;

/// <summary>
/// <see cref="LibraryFilterEvaluator"/> against the Library page's filter chain
/// (<c>frontend/src/pages/LibraryPage.tsx</c>, the <c>visible</c> memo). Each case states what the
/// page does with the same spec and rows; a change to the page's semantics needs the same change here.
/// </summary>
public sealed class LibraryFilterEvaluatorTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
    private static readonly LibraryFilterContext Tracking = new(ReadTracking: true, MaxContentRating: "erotica", Now);

    private static SeriesDto Row(int id, string title, Func<SeriesDto, SeriesDto>? with = null)
    {
        var row = new SeriesDto(
            Id: id, Title: title, DisplayTitle: title, SortTitle: title.ToLowerInvariant(), OriginalTitle: null,
            AltTitles: null, Status: "Ongoing", Type: "manga", Overview: null, Year: 2020, Genres: [],
            MetadataTags: [], ContentRating: "safe", TagIds: [], Monitored: true, MonitorNewItems: "All",
            RootFolderId: 1, FolderName: title, RootFolderPath: null, CoverUrl: null, TotalChapters: null,
            TotalVolumes: null, AuthorStory: null, AuthorArt: null, Publisher: null, Rating: null, MangaBakaId: null,
            AniListId: null, MalId: null, KitsuId: null, Links: null, NumberingClash: null,
            Added: Now.AddDays(-id), WantedChapterCount: 10, ChapterFileCount: 10, KnownChapterCount: 10,
            QueuedCount: 0, DownloadingCount: 0, HasAnime: false, AnimeName: null, AnimeStart: null, AnimeEnd: null);
        return with is null ? row : with(row);
    }

    private static List<int> Ids(IEnumerable<SeriesDto> rows, LibraryFilterSpec spec, LibraryFilterContext? context = null) =>
        LibraryFilterEvaluator.Apply(rows, spec, context ?? Tracking).Select(s => s.Id).ToList();

    [Fact]
    public void An_empty_spec_keeps_every_active_series_newest_added_first()
    {
        var rows = new[] { Row(3, "C"), Row(1, "A"), Row(2, "B"), Row(4, "Archived", r => r with { ArchivedAt = Now }) };

        Assert.Equal([1, 2, 3], Ids(rows, new LibraryFilterSpec()));
    }

    [Fact]
    public void Tags_match_any_or_all_exclude_and_untagged_only_ignores_both()
    {
        var rows = new[]
        {
            Row(1, "Both", r => r with { TagIds = [1, 2] }),
            Row(2, "One", r => r with { TagIds = [1] }),
            Row(3, "None"),
        };

        Assert.Equal([1, 2], Ids(rows, new LibraryFilterSpec(TagIds: [1, 2])));
        Assert.Equal([1], Ids(rows, new LibraryFilterSpec(TagIds: [1, 2], TagMatch: "all")));
        Assert.Equal([2, 3], Ids(rows, new LibraryFilterSpec(ExcludeTagIds: [2])));
        Assert.Equal([3], Ids(rows, new LibraryFilterSpec(TagIds: [1], UntaggedOnly: true)));
    }

    [Fact]
    public void Genres_types_and_reading_status_filter_like_the_page()
    {
        var rows = new[]
        {
            Row(1, "Action manhwa", r => r with { Genres = ["Action"], Type = "manhwa" }),
            Row(2, "Action manga", r => r with { Genres = ["Action", "Drama"], ReadingStatus = "Reading" }),
            Row(3, "Untyped", r => r with { Type = null }),
        };

        Assert.Equal([1, 2], Ids(rows, new LibraryFilterSpec(Genres: ["Action"])));
        Assert.Equal([2], Ids(rows, new LibraryFilterSpec(Genres: ["Action", "Drama"], GenreMatch: "all")));
        Assert.Equal([1, 3], Ids(rows, new LibraryFilterSpec(ExcludeGenres: ["Drama"])));
        // An untyped series only shows while no type is picked.
        Assert.Equal([1], Ids(rows, new LibraryFilterSpec(Types: ["manhwa", "other"])));
        Assert.Equal([2], Ids(rows, new LibraryFilterSpec(ReadingStatuses: ["Reading"])));
    }

    [Fact]
    public void Content_ratings_are_narrowed_to_the_ceiling_and_nothing_left_means_no_filter()
    {
        var rows = new[]
        {
            Row(1, "Safe"),
            Row(2, "Explicit", r => r with { ContentRating = "pornographic" }),
            Row(3, "Unrated", r => r with { ContentRating = null }),
        };

        Assert.Equal([1], Ids(rows, new LibraryFilterSpec(ContentRatings: ["safe", "pornographic"])));
        Assert.Equal([1, 2, 3], Ids(rows, new LibraryFilterSpec(ContentRatings: ["pornographic"])));
    }

    [Fact]
    public void Rating_state_reads_the_so_far_score_when_there_is_no_final_one()
    {
        var rows = new[]
        {
            Row(1, "Rated eight", r => r with { Rating = 8 }),
            Row(2, "So far six", r => r with { ProvisionalRating = 6 }),
            Row(3, "Unrated"),
        };

        Assert.Equal([3], Ids(rows, new LibraryFilterSpec(RatingState: "unrated")));
        Assert.Equal([1, 2], Ids(rows, new LibraryFilterSpec(RatingState: "rated")));
        Assert.Equal([1], Ids(rows, new LibraryFilterSpec(RatingState: "rated", RatingMin: 7)));
    }

    [Fact]
    public void A_narrowed_community_window_drops_unscored_series()
    {
        var rows = new[]
        {
            Row(1, "High", r => r with { CommunityRating = 8.6 }),
            Row(2, "Low", r => r with { CommunityRating = 5.0 }),
            Row(3, "Unscored"),
        };

        Assert.Equal([1, 2, 3], Ids(rows, new LibraryFilterSpec()));
        Assert.Equal([1], Ids(rows, new LibraryFilterSpec(CommunityRatingMin: 7)));
    }

    [Fact]
    public void Completeness_counts_removed_files_as_had()
    {
        var rows = new[]
        {
            Row(1, "Behind", r => r with { ChapterFileCount = 6 }),
            Row(2, "Removed", r => r with { ChapterFileCount = 6, RemovedChapterCount = 4 }),
            Row(3, "Nothing wanted", r => r with { WantedChapterCount = 0, KnownChapterCount = 12, ChapterFileCount = 0 }),
        };

        Assert.Equal([1, 3], Ids(rows, new LibraryFilterSpec(Completeness: "behind")));
        Assert.Equal([2], Ids(rows, new LibraryFilterSpec(Completeness: "complete")));
    }

    [Fact]
    public void Chapter_window_reads_files_or_the_total_and_year_window_drops_unknown_years()
    {
        var rows = new[]
        {
            Row(1, "Short", r => r with { ChapterFileCount = 3, WantedChapterCount = 40 }),
            Row(2, "Long", r => r with { ChapterFileCount = 30, WantedChapterCount = 30, Year = null }),
        };

        Assert.Equal([1], Ids(rows, new LibraryFilterSpec(ChapterMax: 10)));
        Assert.Equal([1], Ids(rows, new LibraryFilterSpec(ChapterMin: 35, ChapterMode: "total")));
        Assert.Equal([1], Ids(rows, new LibraryFilterSpec(YearMin: 2000)));
    }

    [Fact]
    public void Read_window_uses_progress_and_counts_untracked_reading_as_zero()
    {
        var rows = new[]
        {
            Row(1, "Half", r => r with { ReadChapterCount = 5 }),
            Row(2, "Done", r => r with { ReadChapterCount = 10 }),
            Row(3, "Untouched"),
        };

        Assert.Equal([1, 2], Ids(rows, new LibraryFilterSpec(ReadMin: 50)));
        Assert.Equal([1, 3], Ids(rows, new LibraryFilterSpec(ReadMax: 60)));
        Assert.Equal([1, 2, 3], Ids(rows, new LibraryFilterSpec(ReadMax: 60), Tracking with { ReadTracking = false }));
    }

    [Fact]
    public void Source_state_and_sources_match_like_the_page()
    {
        var rows = new[]
        {
            Row(1, "Live", r => r with { Sources = ["a", "b"], EnabledSources = ["a", "b"] }),
            Row(2, "Half off", r => r with { Sources = ["a", "b"], EnabledSources = ["a"], FileSources = ["b"] }),
            Row(3, "All off", r => r with { Sources = ["a"], EnabledSources = [] }),
            Row(4, "Unlinked"),
        };

        Assert.Equal([4], Ids(rows, new LibraryFilterSpec(SourceState: "none")));
        Assert.Equal([2, 3], Ids(rows, new LibraryFilterSpec(SourceState: "hasDisabled")));
        Assert.Equal([3], Ids(rows, new LibraryFilterSpec(SourceState: "noneEnabled")));
        Assert.Equal([1, 2], Ids(rows, new LibraryFilterSpec(Sources: ["a", "b"], SourceMatch: "all")));
        Assert.Equal([2], Ids(rows, new LibraryFilterSpec(FileSources: ["b"])));
    }

    [Fact]
    public void Monitoring_profile_folders_and_per_series_switches()
    {
        var rows = new[]
        {
            Row(1, "Pinned", r => r with { UpgradeProfileId = 7, RootFolderId = 2, Incognito = "Full" }),
            Row(2, "Muted", r => r with { Monitored = false, NotificationMode = "Muted", HasAnime = true }),
            Row(3, "Overridden", r => r with { PageConversion = "High", NumberingClash = "a|b" }),
        };

        Assert.Equal([2], Ids(rows, new LibraryFilterSpec(Monitored: "unmonitored")));
        Assert.Equal([2, 3], Ids(rows, new LibraryFilterSpec(QualityProfile: "default")));
        Assert.Equal([1], Ids(rows, new LibraryFilterSpec(QualityProfile: "7")));
        Assert.Equal([1], Ids(rows, new LibraryFilterSpec(RootFolderIds: [2])));
        Assert.Equal([1], Ids(rows, new LibraryFilterSpec(Incognito: "on")));
        Assert.Equal([2], Ids(rows, new LibraryFilterSpec(Muted: "muted")));
        Assert.Equal([2], Ids(rows, new LibraryFilterSpec(Anime: "has")));
        Assert.Equal([3], Ids(rows, new LibraryFilterSpec(NumberingClash: "clash")));
        Assert.Equal([3], Ids(rows, new LibraryFilterSpec(Overrides: ["pageConversion"])));
        Assert.Empty(Ids(rows, new LibraryFilterSpec(Overrides: ["readCleanup"])));
    }

    [Fact]
    public void Credits_split_on_commas_and_publishers_and_added_within_match_exactly()
    {
        var rows = new[]
        {
            Row(1, "Duo", r => r with { AuthorStory = "Ito, Kanno", Publisher = "Kodansha" }),
            Row(40, "Old", r => r with { AuthorArt = "Kanno" }),
        };

        Assert.Equal([1, 40], Ids(rows, new LibraryFilterSpec(Credits: ["Kanno"])));
        Assert.Equal([1], Ids(rows, new LibraryFilterSpec(Publishers: ["Kodansha"])));
        Assert.Equal([1], Ids(rows, new LibraryFilterSpec(AddedWithinDays: 30)));
    }

    [Fact]
    public void Sorts_keep_missing_values_last_whichever_way_they_face()
    {
        var rows = new[]
        {
            Row(1, "The Beta", r => r with { SortTitle = "beta", Year = 2001, LastReadAt = Now.AddDays(-3) }),
            Row(2, "Alpha", r => r with { Year = null, LastReadAt = Now.AddDays(-1) }),
            Row(3, "Gamma", r => r with { Year = 1999 }),
        };

        Assert.Equal([1, 3, 2], Ids(rows, new LibraryFilterSpec(Sort: "year")));
        Assert.Equal([3, 1, 2], Ids(rows, new LibraryFilterSpec(Sort: "year", SortDir: "asc")));
        Assert.Equal([2, 1, 3], Ids(rows, new LibraryFilterSpec(Sort: "read")));
        // A display title equal to the canonical one sorts by the stored sort title.
        Assert.Equal([2, 1, 3], Ids(rows, new LibraryFilterSpec(Sort: "title")));
        Assert.Equal([3, 1, 2], Ids(rows, new LibraryFilterSpec(Sort: "title", SortDir: "desc")));
    }

    [Fact]
    public void Status_sorts_by_lifecycle_and_unread_by_files_left()
    {
        var rows = new[]
        {
            Row(1, "Done", r => r with { Status = "Completed", ReadChapterCount = 10 }),
            Row(2, "Paused", r => r with { Status = "Hiatus", ReadChapterCount = 2 }),
            Row(3, "Running", r => r with { Status = "Ongoing" }),
        };

        Assert.Equal([3, 2, 1], Ids(rows, new LibraryFilterSpec(Sort: "status")));
        Assert.Equal([3, 2, 1], Ids(rows, new LibraryFilterSpec(Sort: "unread")));
    }

    [Fact]
    public void Search_matches_any_title_ignoring_case_marks_and_spacing()
    {
        var rows = new[]
        {
            Row(1, "One Piece"),
            Row(2, "Pokémon Adventures"),
            Row(3, "Different", r => r with { AltTitleNames = ["Kimetsu no Yaiba"] }),
            Row(4, "Something else"),
        };

        Assert.Equal([1], Ids(rows, new LibraryFilterSpec(Query: "onepiece")));
        Assert.Equal([2], Ids(rows, new LibraryFilterSpec(Query: "pokemon adv")));
        Assert.Equal([3], Ids(rows, new LibraryFilterSpec(Query: "kimetsu yai")));
        Assert.Empty(Ids(rows, new LibraryFilterSpec(Query: "zzz")));
    }
}
