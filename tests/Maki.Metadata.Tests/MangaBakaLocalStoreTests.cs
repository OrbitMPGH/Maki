using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Metadata.Catalogue;
using Maki.Metadata.MangaBaka;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Metadata.Tests;

public class MangaBakaLocalStoreTests : IDisposable
{
    private readonly DumpDbBuilder _db = new();
    private readonly FakeAppSettings _settings = new();

    private MangaBakaLocalStore Store => new(
        new MangaBakaDumpOptions(_db.Path, Path.GetTempPath()),
        _settings,
        NullLogger<MangaBakaLocalStore>.Instance);

    /// <summary>A store wired to the catalogue indexes, so typo tolerance and credits are live.</summary>
    private MangaBakaLocalStore Catalogued(CatalogueOptions? options = null)
    {
        var dumpOptions = new MangaBakaDumpOptions(_db.Path, Path.GetTempPath());
        return new MangaBakaLocalStore(
            dumpOptions,
            _settings,
            NullLogger<MangaBakaLocalStore>.Instance,
            new CatalogueIndexCache(dumpOptions, NullLogger<CatalogueIndexCache>.Instance),
            options ?? CatalogueOptions.Default);
    }

    /// <summary>The term dictionary for this fixture, for the expression-builder tests.</summary>
    private FuzzyTermIndex Terms()
    {
        var dumpOptions = new MangaBakaDumpOptions(_db.Path, Path.GetTempPath());
        var cache = new CatalogueIndexCache(dumpOptions, NullLogger<CatalogueIndexCache>.Instance);
        return cache.GetAsync().GetAwaiter().GetResult()!.Terms;
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Search_finds_by_primary_title_case_insensitive()
    {
        _db.AddSeries(377, "ONE PIECE", status: "releasing", year: 1997, totalChapters: "1187")
            .AddSeries(1, "Berserk")
            .BuildSearchIndex();

        var results = await Store.SearchAsync("one piece", ContentRating.Pornographic);

        var hit = Assert.Single(results);
        Assert.Equal("377", hit.ProviderId);
        Assert.Equal("ONE PIECE", hit.Title);
        Assert.Equal(1997, hit.Year);
        Assert.Equal(SeriesStatus.Ongoing, hit.Status);
        Assert.Equal(1187, hit.TotalChapters);
    }

    [Fact]
    public async Task Search_finds_by_alternative_title_from_titles_json()
    {
        _db.AddSeries(42, "Shingeki no Kyojin",
                titlesJson: """[{"title": "Attack on Titan", "language": "en", "is_primary": true}]""")
            .BuildSearchIndex();

        var results = await Store.SearchAsync("attack on titan", ContentRating.Pornographic);

        Assert.Equal("42", Assert.Single(results).ProviderId);
    }

    [Fact]
    public async Task Search_matches_prefix_of_last_token()
    {
        _db.AddSeries(7, "Fullmetal Alchemist").BuildSearchIndex();

        var results = await Store.SearchAsync("fullmetal alch", ContentRating.Pornographic);

        Assert.Single(results);
    }

    [Theory]
    [InlineData("what was I meant to call this mess that wouldn’t go away")]
    [InlineData("WHAT WAS I MEANT TO CALL THIS MESS THAT WOULDN'T GO AWAY?")]
    [InlineData("  What Was I Meant to Call This Mess\nThat Wouldn't Go Away?  ")]
    [InlineData("Ochinai Yogore wo Boku wa Nanto Yobeba Yokatta noka")]
    [InlineData("落ちない汚れを僕は何と呼べばよかったのか")]
    public async Task Exact_title_lookup_checks_all_variants_and_normalizes_punctuation(string query)
    {
        _db.AddSeries(353915, "Ochinai Yogore wo Boku wa Nanto Yobeba Yokatta noka",
                nativeTitle: "落ちない汚れを僕は何と呼べばよかったのか",
                titlesJson: """
                    [{"title":"What Was I Meant to Call This Mess That Wouldn't Go Away?","language":"en","is_primary":true},
                     {"title":"What Was I Meant to Call This Mess That Wouldn't Go Away?","language":"en","is_primary":false}]
                    """)
            .AddSeries(111565, "Mikoto-chan Doesn't Want to Be Hated!")
            .BuildSearchIndex();

        Assert.Equal(353915L, Assert.Single(await Store.GetExactTitleIdsAsync(query)));
    }

    [Theory]
    [InlineData("mess that wouldn't go away")]
    [InlineData("wouldn't mess away go")]
    [InlineData("What Was I Meant to Call This Mes")]
    [InlineData("?!")]
    [InlineData("")]
    public async Task Exact_title_lookup_rejects_partial_and_reordered_queries(string query)
    {
        _db.AddSeries(1, "What Was I Meant to Call This Mess That Wouldn't Go Away?")
            .BuildSearchIndex();

        Assert.Empty(await Store.GetExactTitleIdsAsync(query));
    }

    [Theory]
    [InlineData("what was I meant to call this mess that wouldn’t go awya", 1)]
    [InlineData("what was I meant to call this mes that wouldn’t go away", 1)]
    [InlineData("what was I meant to call this mass that wouldn’t go away", 1)]
    [InlineData("what was I meant to call this mes that wouldn’t go awya", 2)]
    public async Task Near_title_lookup_tolerates_small_typos_in_long_alternative_titles(string query, int distance)
    {
        _db.AddSeries(353915, "Ochinai Yogore wo Boku wa Nanto Yobeba Yokatta noka",
                titlesJson: """[{"title":"What Was I Meant to Call This Mess That Wouldn't Go Away?"}]""")
            .AddSeries(111565, "Mikoto-chan Doesn't Want to Be Hated!")
            .AddSeries(2, "Mass")
            .BuildSearchIndex();

        var match = Assert.Single(await Catalogued().GetNearTitleIdsAsync(query));

        Assert.Equal(353915L, match.Key);
        Assert.Equal(distance, match.Value);
    }

    [Theory]
    [InlineData("bersrek", "Berserk")]
    [InlineData("bersek", "Berserk")]
    [InlineData("tokyo ghol", "Tokyo Ghoul")]
    [InlineData("Asahichan", "Asahi-chan")]
    public async Task Near_title_lookup_reuses_spelling_and_compound_search(string query, string title)
    {
        _db.AddSeries(1, "A different main title", titlesJson: System.Text.Json.JsonSerializer.Serialize(
                new[] { new { title } }))
            .BuildSearchIndex();

        Assert.Equal(1L, Assert.Single(await Catalogued().GetNearTitleIdsAsync(query)).Key);
    }

    [Theory]
    [InlineData("what was I meant to call this mess")]
    [InlineData("mess this call to meant I was what that wouldn't go away")]
    [InlineData("what was I ment to call ths mes that wouldn’t go awya")]
    public async Task Near_title_lookup_does_not_promote_fragments_or_loose_matches(string query)
    {
        _db.AddSeries(1, "What Was I Meant to Call This Mess That Wouldn't Go Away?")
            .BuildSearchIndex();

        Assert.Empty(await Catalogued().GetNearTitleIdsAsync(query));
    }

    [Fact]
    public async Task Exact_title_lookup_is_not_limited_by_lexical_candidate_depth()
    {
        for (var i = 1; i <= 30; i++)
        {
            _db.AddSeries(i, "The Same Title", popularity: i);
        }

        _db.AddSeries(31, "The Same Title: A Sequel")
            .AddSeries(32, "The Same Title", state: "merged", mergedWith: "1")
            .BuildSearchIndex();

        var ids = await Store.GetExactTitleIdsAsync("the same title");

        Assert.Equal(Enumerable.Range(1, 30).Select(i => (long)i), ids.Order());
    }

    [Theory]
    [InlineData("I want to put the cheeky Asahichan in her place")]
    [InlineData("I want to put the cheeky Asahi-chan in her place")]
    [InlineData("I want to teach that cheeky Asahichan a lesson")]
    [InlineData("Namaiki Asahichan o Wakarasetai")]
    [InlineData("ナマイキ旭ちゃんをわからせたい")]
    [InlineData("I Wanna Set This Cocky Asahichan Straight")]
    public async Task Search_title_variants_resolve_to_one_canonical_series(string query)
    {
        const string title = "I Wanna Set This Cocky Asahi-chan Straight";
        _db.AddSeries(351135, title,
                nativeTitle: "ナマイキ旭ちゃんをわからせたい",
                romanizedTitle: "Namaiki Asahi-chan o Wakarasetai",
                titlesJson: """
                    [{"title":"I Wanna Set This Cocky Asahi-chan Straight","language":"en","is_primary":true},
                     {"title":"I Want to Put the Cheeky Asahi-chan in Her Place","language":"en","is_primary":false},
                     {"title":"I Want to Teach that Cheeky Asahi-chan a Lesson","language":"en","is_primary":false}]
                    """)
            .BuildSearchIndex();

        // This is also the lexical entry point used by smart search.
        var outcome = await Catalogued().SearchWithCorrectionAsync(query, ContentRating.Safe);

        var hit = Assert.Single(outcome.Items);
        Assert.Equal("351135", hit.ProviderId);
        Assert.Equal(title, hit.Title);
        Assert.Null(outcome.CorrectedQuery);
    }

    [Fact]
    public async Task Joined_title_words_preserve_exact_hits_filters_and_deduplication()
    {
        _db.AddSeries(1, "Asahichan")
            .AddSeries(2, "Asahi-chan", titlesJson: """[{"title":"Asahi chan"}]""")
            .AddSeries(3, "Asahi-chan", contentRating: "pornographic")
            .AddSeries(4, "Asahi-chan", type: "novel")
            .AddSeries(5, "Asahi-chan", state: "merged", mergedWith: "2")
            .AddSeries(6, "Asahi meets Chan")
            .BuildSearchIndex();

        var store = Catalogued();
        var outcome = await store.SearchWithCorrectionAsync("Asahichan", ContentRating.Safe);
        Assert.Equal(["1", "2"], outcome.Items.Select(hit => hit.ProviderId));

        var restricted = await store.SearchWithCorrectionAsync(
            "Asahichan", ContentRating.Safe, restrictToIds: [2L]);
        Assert.Equal("2", Assert.Single(restricted.Items).ProviderId);

        var limited = await store.SearchWithCorrectionAsync("Asahichan", ContentRating.Safe, limit: 1);
        Assert.Equal("1", Assert.Single(limited.Items).ProviderId);
    }

    [Fact]
    public async Task Joined_title_words_must_be_adjacent_within_one_variant()
    {
        _db.AddSeries(1, "Asahi meets Chan")
            .AddSeries(2, "Asahi", titlesJson: """[{"title":"Chan"}]""")
            .BuildSearchIndex();

        var outcome = await Catalogued().SearchWithCorrectionAsync("Asahichan", ContentRating.Safe);

        Assert.Empty(outcome.Items);
    }

    [Fact]
    public async Task Search_excludes_merged_series()
    {
        _db.AddSeries(10, "Naruto", state: "merged", mergedWith: "11")
            .AddSeries(11, "Naruto")
            .BuildSearchIndex();

        var results = await Store.SearchAsync("naruto", ContentRating.Pornographic);

        Assert.Equal("11", Assert.Single(results).ProviderId);
    }

    [Fact]
    public async Task Search_ranks_popular_series_first_on_equal_match()
    {
        _db.AddSeries(1, "Bleach", popularity: 500)
            .AddSeries(2, "Bleach", popularity: 3)
            .BuildSearchIndex();

        var results = await Store.SearchAsync("bleach", ContentRating.Pornographic);

        Assert.Equal("2", results[0].ProviderId);
    }

    [Fact]
    public async Task Search_applies_the_callers_content_rating_ceiling()
    {
        _db.AddSeries(1, "Ceiling Test", contentRating: "safe")
            .AddSeries(2, "Ceiling Test", contentRating: "suggestive")
            .AddSeries(3, "Ceiling Test", contentRating: "erotica")
            .AddSeries(4, "Ceiling Test", contentRating: "pornographic")
            .BuildSearchIndex();

        var safe = await Store.SearchAsync("ceiling test", ContentRating.Safe);
        var suggestive = await Store.SearchAsync("ceiling test", ContentRating.Suggestive);

        // The ceiling is the caller's own MakiUser.MaxContentRating. It used to be read from the
        // instance-wide discover.maxcontentrating, which the PerUserData migration deletes, so every
        // user was silently filtered at the permissive default no matter what their account said.
        Assert.Equal(["1"], safe.Select(r => r.ProviderId));
        Assert.Equal(["1", "2"], suggestive.Select(r => r.ProviderId).Order());
    }

    [Fact]
    public async Task Search_fails_closed_on_a_ceiling_it_does_not_recognize()
    {
        _db.AddSeries(1, "Fallback", contentRating: "safe")
            .AddSeries(2, "Fallback", contentRating: "erotica")
            .BuildSearchIndex();

        // An empty ceiling is what an unauthenticated CurrentUserContext carries. Falling back to
        // the permissive default here would make "no user" the least restricted caller there is.
        var results = await Store.SearchAsync("fallback", string.Empty);

        Assert.Equal("1", Assert.Single(results).ProviderId);
    }

    [Fact]
    public async Task Get_drops_tags_the_series_marks_as_spoilers()
    {
        _db.AddSeries(1, "Dandadan",
            tagsJson: """["Youkai", "Amnesia", "Aliens", "Body Horror"]""",
            tagsV2Json: """
                [{"name":"Youkai","weight":"core"},
                 {"name":"Amnesia","weight":"recurrent","is_spoiler":true},
                 {"name":"body horror","weight":"incidental","is_spoiler":true}]
                """);

        var metadata = await Store.GetAsync("1");

        // "Aliens" survives despite having no tags_v2 entry; the match is case-insensitive.
        Assert.Equal(["Youkai", "Aliens"], metadata!.Tags);
    }

    [Fact]
    public async Task Get_keeps_every_tag_when_the_series_has_no_tags_v2()
    {
        _db.AddSeries(1, "Old Entry", tagsJson: """["Amnesia", "Pirates"]""");

        var metadata = await Store.GetAsync("1");

        Assert.Equal(["Amnesia", "Pirates"], metadata!.Tags);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"name":"Amnesia","is_spoiler":true}""")]
    [InlineData("""[{"is_spoiler":true},"loose string",null]""")]
    public void Malformed_tags_v2_leaves_the_tag_list_untouched(string json)
    {
        List<string> tags = ["Amnesia", "Pirates"];

        Assert.Equal(["Amnesia", "Pirates"], MangaBakaLocalStore.WithoutSpoilerTags(tags, json));
    }

    [Fact]
    public async Task Get_maps_all_fields()
    {
        _db.AddSeries(377, "ONE PIECE",
            nativeTitle: "ãƒ¯ãƒ³ãƒ”ãƒ¼ã‚¹",
            description: "Pirates.",
            year: 1997,
            status: "releasing",
            finalVolume: "115",
            totalChapters: "1187",
            authorsJson: """["Eiichirou Oda"]""",
            artistsJson: """["Eiichirou Oda", "Someone Else"]""",
            genresJson: """["Action", "Adventure"]""",
            tagsJson: """["Pirates"]""",
            coverUrl: "https://images.mangabaka.dev/cover.png",
            aniListId: 30013,
            malId: 13,
            mangaUpdatesId: "pb8uwds");

        var metadata = await Store.GetAsync("377");

        Assert.NotNull(metadata);
        Assert.Equal("377", metadata.ProviderId);
        Assert.Equal("ONE PIECE", metadata.Title);
        Assert.Equal("ãƒ¯ãƒ³ãƒ”ãƒ¼ã‚¹", metadata.OriginalTitle);
        Assert.Equal("Pirates.", metadata.Description);
        Assert.Equal(1997, metadata.Year);
        Assert.Equal(SeriesStatus.Ongoing, metadata.Status);
        Assert.Equal(115, metadata.TotalVolumes);
        Assert.Equal(1187, metadata.TotalChapters);
        Assert.Equal("Eiichirou Oda", metadata.AuthorStory);
        Assert.Equal("Eiichirou Oda, Someone Else", metadata.AuthorArt);
        Assert.Equal(["Action", "Adventure"], metadata.Genres);
        Assert.Equal(["Pirates"], metadata.Tags);
        Assert.Equal("https://images.mangabaka.dev/cover.png", metadata.CoverUrl);
        Assert.Equal("https://mangabaka.org/377", metadata.WebUrl);
        Assert.Equal(377, metadata.MangaBakaId);
        Assert.Equal(30013, metadata.AniListId);
        Assert.Equal(13, metadata.MalId);
        Assert.Equal("pb8uwds", metadata.MangaUpdatesId);
    }

    [Fact]
    public async Task Get_parses_fractional_chapter_counts()
    {
        _db.AddSeries(5, "Some Series", totalChapters: "112.5");

        var metadata = await Store.GetAsync("5");

        Assert.Equal(112, metadata!.TotalChapters);
    }

    [Fact]
    public async Task Get_follows_merged_chain_to_canonical_series()
    {
        _db.AddSeries(10, "Old Entry", state: "merged", mergedWith: "20")
            .AddSeries(20, "Canonical Entry");

        var metadata = await Store.GetAsync("10");

        Assert.Equal("20", metadata!.ProviderId);
        Assert.Equal("Canonical Entry", metadata.Title);
    }

    [Fact]
    public async Task Get_returns_null_for_unknown_or_invalid_ids()
    {
        _db.AddSeries(1, "Something");

        Assert.Null(await Store.GetAsync("999"));
        Assert.Null(await Store.GetAsync("not-a-number"));
    }

    [Fact]
    public async Task IsAvailable_depends_on_file_and_setting()
    {
        Assert.True(await Store.IsAvailableAsync());

        _settings.Values[SettingKeys.MangaBakaUseLocalDb] = "false";
        Assert.False(await Store.IsAvailableAsync());

        _settings.Values[SettingKeys.MangaBakaUseLocalDb] = "true";
        var missingFile = new MangaBakaLocalStore(
            new MangaBakaDumpOptions(Path.Combine(Path.GetTempPath(), "does-not-exist.db"), Path.GetTempPath()),
            _settings,
            NullLogger<MangaBakaLocalStore>.Instance);
        Assert.False(await missingFile.IsAvailableAsync());
    }

    // --- typo tolerance ---------------------------------------------------------------------

    [Fact]
    public async Task Search_rescues_a_misspelled_title()
    {
        _db.AddSeries(1, "Berserk").BuildSearchIndex();

        var outcome = await Catalogued().SearchWithCorrectionAsync("berserck", ContentRating.Pornographic);

        Assert.Equal("1", Assert.Single(outcome.Items).ProviderId);
        Assert.Equal("berserk", outcome.CorrectedQuery);
    }

    [Fact]
    public async Task Search_without_the_catalogue_indexes_stays_exact()
    {
        _db.AddSeries(1, "Berserk").BuildSearchIndex();

        var outcome = await Store.SearchWithCorrectionAsync("berserck", ContentRating.Pornographic);

        Assert.Empty(outcome.Items);
        Assert.Null(outcome.CorrectedQuery);
    }

    /// <summary>
    /// A query that already works never pays for the second FTS round trip, and never risks a
    /// correction displacing the spelling that matched.
    /// </summary>
    [Fact]
    public async Task Search_skips_the_rescue_when_the_exact_pass_answered()
    {
        for (var i = 1; i <= 6; i++)
        {
            _db.AddSeries(i, $"Berserk Volume {i}");
        }

        _db.AddSeries(10, "Berserker Rage").BuildSearchIndex();

        var outcome = await Catalogued().SearchWithCorrectionAsync("berserk", ContentRating.Pornographic);

        Assert.Null(outcome.CorrectedQuery);
    }

    /// <summary>
    /// Appending rather than merging by score is the whole guarantee: a respelling can never push
    /// a title that genuinely matched down the page. The fusion upstream reads this order as ranks.
    /// </summary>
    [Fact]
    public async Task Rescued_rows_come_after_exact_ones()
    {
        // "Berserk" has to be the clearly more common spelling for the rescue to offer it at all;
        // see FuzzyOptions.MinCorrectionDominance.
        _db.AddSeries(1, "Bersek Chronicles");
        for (var i = 2; i <= 6; i++)
        {
            _db.AddSeries(i, $"Berserk {i}");
        }

        _db.BuildSearchIndex();

        var outcome = await Catalogued().SearchWithCorrectionAsync("bersek", ContentRating.Pornographic);

        Assert.Equal("1", outcome.Items[0].ProviderId);
        Assert.True(outcome.Items.Count > 1, "the rescue should have added the correctly spelled titles");
        // No correction is reported: "bersek" is itself a word in this fixture's index, so the user
        // was not told they mistyped even though the widened query found more.
        Assert.Null(outcome.CorrectedQuery);
    }

    [Fact]
    public async Task Fuzzy_can_be_turned_off()
    {
        _db.AddSeries(1, "Berserk").BuildSearchIndex();

        var options = CatalogueOptions.Default with
        {
            Fuzzy = FuzzyOptions.Default with { Enabled = false },
        };

        var outcome = await Catalogued(options).SearchWithCorrectionAsync("berserck", ContentRating.Pornographic);
        Assert.Empty(outcome.Items);
    }

    // --- credits ----------------------------------------------------------------------------

    [Fact]
    public async Task A_bare_author_term_lists_their_works_by_popularity()
    {
        _db.AddSeries(1, "Later", authorsJson: """["Junji Ito"]""", popularity: 900)
            .AddSeries(2, "Famous", authorsJson: """["Junji Ito"]""", popularity: 3)
            .AddSeries(3, "Somebody Else", authorsJson: """["Other Person"]""")
            .BuildSearchIndex();

        var outcome = await Catalogued().SearchWithCorrectionAsync(
            "author:\"Junji Ito\"", ContentRating.Pornographic);

        Assert.Equal(["2", "1"], outcome.Items.Select(i => i.ProviderId));
        Assert.Equal("Junji Ito", Assert.Single(outcome.Credits).Name);
    }

    [Fact]
    public async Task A_credit_term_narrows_the_title_search()
    {
        _db.AddSeries(1, "Uzumaki", authorsJson: """["Junji Ito"]""")
            .AddSeries(2, "Uzumaki Doppelganger", authorsJson: """["Other Person"]""")
            .BuildSearchIndex();

        var outcome = await Catalogued().SearchWithCorrectionAsync(
            "author:\"Junji Ito\" uzumaki", ContentRating.Pornographic);

        Assert.Equal("1", Assert.Single(outcome.Items).ProviderId);
    }

    [Fact]
    public async Task An_unquoted_author_value_still_searches_its_leftover_words()
    {
        _db.AddSeries(1, "Uzumaki", authorsJson: """["Junji Ito"]""")
            .AddSeries(2, "Tomie", authorsJson: """["Junji Ito"]""")
            .BuildSearchIndex();

        var outcome = await Catalogued().SearchWithCorrectionAsync(
            "author:junji ito uzumaki", ContentRating.Pornographic);

        Assert.Equal("1", Assert.Single(outcome.Items).ProviderId);
    }

    /// <summary>An author nobody has is an answer of "nothing", not an unfiltered title search.</summary>
    [Fact]
    public async Task An_unknown_author_returns_nothing_rather_than_everything()
    {
        _db.AddSeries(1, "Berserk", authorsJson: """["Kentaro Miura"]""").BuildSearchIndex();

        var outcome = await Catalogued().SearchWithCorrectionAsync(
            "author:\"Nobody\" berserk", ContentRating.Pornographic);

        Assert.Empty(outcome.Items);
    }

    [Fact]
    public async Task A_studio_term_matches_publishers()
    {
        _db.AddSeries(1, "A", publishersJson: """[{"name": "Shueisha"}]""")
            .AddSeries(2, "B", publishersJson: """[{"name": "Kodansha"}]""")
            .BuildSearchIndex();

        var outcome = await Catalogued().SearchWithCorrectionAsync("studio:shueisha", ContentRating.Pornographic);

        Assert.Equal("1", Assert.Single(outcome.Items).ProviderId);
    }

    [Fact]
    public async Task An_explicit_id_restriction_bounds_the_results()
    {
        _db.AddSeries(1, "Berserk").AddSeries(2, "Berserk Gaiden").BuildSearchIndex();

        var outcome = await Catalogued().SearchWithCorrectionAsync(
            "berserk", ContentRating.Pornographic, restrictToIds: [2L]);

        Assert.Equal("2", Assert.Single(outcome.Items).ProviderId);
    }

    /// <summary>An empty restriction is "nobody matched", not "no restriction".</summary>
    [Fact]
    public async Task An_empty_id_restriction_returns_nothing()
    {
        _db.AddSeries(1, "Berserk").BuildSearchIndex();

        var outcome = await Catalogued().SearchWithCorrectionAsync(
            "berserk", ContentRating.Pornographic, restrictToIds: []);

        Assert.Empty(outcome.Items);
    }

    [Fact]
    public void BuildFuzzyMatchExpression_is_an_and_of_ors_and_stars_only_the_original()
    {
        _db.AddSeries(1, "Berserk").AddSeries(2, "Saga").BuildSearchIndex();

        var expression = MangaBakaLocalStore.BuildFuzzyMatchExpression(
            "berserck saga", Terms(), FuzzyOptions.Default, out var corrected);

        // Flattening this into one OR would return every title containing any spelling of any
        // token, which on a two-word query is most of the catalogue.
        Assert.NotNull(expression);
        Assert.Contains(" AND ", expression);
        Assert.Contains("\"berserck\" OR \"berserk\"", expression);
        // The prefix star belongs to the original last token, never to a guessed spelling.
        Assert.Contains("\"saga\" *", expression);
        Assert.DoesNotContain("\"berserk\" *", expression);
        Assert.Equal("berserk saga", corrected);
    }

    /// <summary>
    /// The "showing results for ..." line must not rewrite a word the index already knows. Widening
    /// "vinland" to "island" is a reasonable thing to OR into the query and a nonsense thing to tell
    /// somebody they searched for.
    /// </summary>
    [Fact]
    public void The_corrected_query_only_rewrites_words_the_index_has_never_seen()
    {
        _db.AddSeries(1, "Vinland Saga");
        for (var i = 2; i <= 6; i++)
        {
            _db.AddSeries(i, $"Island Story {i}");
        }

        _db.BuildSearchIndex();

        MangaBakaLocalStore.BuildFuzzyMatchExpression(
            "vinland sga", Terms(), FuzzyOptions.Default, out var corrected);

        Assert.True(corrected is null || corrected.StartsWith("vinland ", StringComparison.Ordinal));
    }

    /// <summary>
    /// A token one edit from several different words has no single correction, only a most common
    /// one. All of them still go into the query; none of them is claimed as what the user meant.
    /// </summary>
    [Fact]
    public void An_ambiguous_token_is_not_claimed_as_a_correction()
    {
        _db.AddSeries(1, "Saga").AddSeries(2, "Sea").AddSeries(3, "Ska").BuildSearchIndex();

        var expression = MangaBakaLocalStore.BuildFuzzyMatchExpression(
            "sga", Terms(), FuzzyOptions.Default, out var corrected);

        Assert.NotNull(expression);
        Assert.Contains("saga", expression);
        Assert.Null(corrected);
    }

    [Fact]
    public void BuildFuzzyMatchExpression_is_null_when_nothing_needs_respelling()
    {
        _db.AddSeries(1, "Berserk").BuildSearchIndex();

        Assert.Null(MangaBakaLocalStore.BuildFuzzyMatchExpression(
            "berserk", Terms(), FuzzyOptions.Default, out _));
    }

    /// <summary>
    /// Trending has to rank on the ratio of the two ranks, not their difference. Rank positions are
    /// far denser in the tail than at the head, so a difference makes a drifting no-name in the
    /// 100k band beat any real mover near the front, which is the whole reason the rail filled up
    /// with one corner of the catalogue.
    /// </summary>
    [Fact]
    public async Task Trending_ranks_on_relative_climb_not_absolute_rank_positions()
    {
        _db.AddSeries(1, "Real Mover", rating: 8.0, coverUrl: "c", popularity: 100, popularityHistory1Mo: 900)
            .AddSeries(2, "Tail Drifter", rating: 8.0, coverUrl: "c", popularity: 2900, popularityHistory1Mo: 2950);

        var rail = await Store.GetBrowseAsync(BrowseFeed.Trending, 10);

        Assert.Equal(["Real Mover", "Tail Drifter"], rail.Select(r => r.Title));
    }

    /// <summary>
    /// The rail is a shallow slice on purpose: outside it a rank move doesn't mean the same thing,
    /// and the deep tail is where the noise lives.
    /// </summary>
    [Fact]
    public async Task Trending_excludes_titles_below_the_popularity_ceiling()
    {
        _db.AddSeries(1, "Deep Tail Spike", rating: 8.0, coverUrl: "c", popularity: 60_000, popularityHistory1Mo: 250_000);

        Assert.Empty(await Store.GetBrowseAsync(BrowseFeed.Trending, 10));
    }

    /// <summary>
    /// A title with no history to compare against is not trending, it is unmeasured. Included
    /// because the dump's 1d and 1w history columns are null for every row, which is what a naive
    /// "shorter window" change would silently rank on.
    /// </summary>
    [Fact]
    public async Task Trending_skips_rows_with_no_popularity_history()
    {
        _db.AddSeries(1, "No History", rating: 8.0, coverUrl: "c", popularity: 50)
            .AddSeries(2, "Climber", rating: 8.0, coverUrl: "c", popularity: 400, popularityHistory1Mo: 1200);

        var rail = await Store.GetBrowseAsync(BrowseFeed.Trending, 10);

        Assert.Equal(["Climber"], rail.Select(r => r.Title));
    }

    /// <summary>
    /// The rails apply whatever ceiling the caller passes and hold no floor of their own — the
    /// Discover page builds one cached set per viewer ceiling, so a hardcoded one here would
    /// override the account's setting in both directions.
    /// </summary>
    [Fact]
    public async Task A_browse_rail_honours_the_callers_content_rating_ceiling()
    {
        _db.AddSeries(1, "Safe One", rating: 8.0, coverUrl: "c", popularity: 10, contentRating: "safe")
            .AddSeries(2, "Explicit One", rating: 9.0, coverUrl: "c", popularity: 20, contentRating: "pornographic");

        var restricted = await Store.GetBrowseAsync(
            BrowseFeed.Popular, 10, filters: new RecommendationFilters(ContentRatings: ContentRating.Allowed(ContentRating.Safe)));
        var permitted = await Store.GetBrowseAsync(
            BrowseFeed.Popular, 10, filters: new RecommendationFilters(ContentRatings: ContentRating.Allowed(ContentRating.Pornographic)));

        Assert.Equal(["Safe One"], restricted.Select(r => r.Title));
        Assert.Equal(["Safe One", "Explicit One"], permitted.Select(r => r.Title));
    }

    [Theory]
    [InlineData("one piece", "\"one\" \"piece\" *")]
    [InlineData("solo", "\"solo\" *")]
    [InlineData("with \"quotes\"", "\"with\" \"quotes\" *")]
    [InlineData("   ", null)]
    [InlineData("\"\"", null)]
    public void BuildMatchExpression_quotes_tokens_and_prefixes_last(string query, string? expected)
    {
        Assert.Equal(expected, MangaBakaLocalStore.BuildMatchExpression(query));
    }

    [Fact]
    public async Task Profile_rows_carry_what_a_taste_profile_aggregates()
    {
        _db.AddSeries(
            1, "Profiled",
            year: 2011,
            type: "manhwa",
            genresJson: """["Action", "Drama"]""",
            authorsJson: """["Kousei Eguchi"]""",
            artistsJson: """["George Morikawa"]""",
            tagsV2Json: """
                [
                  {"name": "Time Travel", "weight": "core", "is_genre": false},
                  {"name": "Amnesia", "weight": "core", "is_genre": false, "is_spoiler": true},
                  {"name": "Action", "weight": "core", "is_genre": true},
                  {"name": "Background Noise", "weight": "unweighted", "is_genre": false}
                ]
                """);

        var row = (await Store.GetProfileRowsAsync([1L]))[1];

        Assert.Equal(2011, row.Year);
        Assert.Equal("manhwa", row.Type);
        Assert.Equal(["Action", "Drama"], row.Genres);
        Assert.Equal(["Kousei Eguchi"], row.Authors);
        Assert.Equal(["George Morikawa"], row.Artists);

        // Genre tags and the unweighted bucket are dropped by the shared parser; the spoiler is kept
        // here and flagged, because only the caller knows whether it is showing it to the reader.
        Assert.Equal(["Amnesia", "Time Travel"], row.Tags.Select(t => t.Name).Order());
        Assert.True(row.Tags.Single(t => t.Name == "Amnesia").IsSpoiler);
    }

    [Fact]
    public async Task Profile_rows_skip_ids_the_dump_does_not_have()
    {
        _db.AddSeries(1, "Present");

        var rows = await Store.GetProfileRowsAsync([1L, 999L]);

        Assert.Equal([1L], rows.Keys);
    }

    [Fact]
    public async Task No_ids_never_opens_the_dump()
    {
        // A store pointed at nothing: the short-circuit is what keeps an empty library from throwing.
        var missingFile = new MangaBakaLocalStore(
            new MangaBakaDumpOptions(Path.Combine(Path.GetTempPath(), "nope.db"), Path.GetTempPath()),
            _settings,
            NullLogger<MangaBakaLocalStore>.Instance);

        Assert.Empty(await missingFile.GetProfileRowsAsync([]));
    }
}
