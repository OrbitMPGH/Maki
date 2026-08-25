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
}
