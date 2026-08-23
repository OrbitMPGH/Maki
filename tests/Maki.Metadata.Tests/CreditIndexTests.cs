using Maki.Metadata.Catalogue;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Metadata.Tests;

public class CreditIndexTests : IDisposable
{
    private readonly DumpDbBuilder _db = new();

    public void Dispose() => _db.Dispose();

    private CreditIndex Build()
    {
        using var conn = new SqliteConnection($"Data Source={_db.Path};Mode=ReadOnly;Pooling=False");
        conn.Open();
        return CreditIndex.Build(conn, NullLogger.Instance);
    }

    [Fact]
    public void Reads_authors_artists_and_publishers()
    {
        _db.AddSeries(
            1, "Berserk",
            authorsJson: """["Kentaro Miura"]""",
            artistsJson: """["Kentaro Miura"]""",
            publishersJson: """[{"name": "Hakusensha", "note": null, "type": "Original"}]""");

        var index = Build();

        Assert.True(index.TryResolve("Kentaro Miura", CreditRole.Author, out var author));
        Assert.Equal(CreditRole.Author | CreditRole.Artist, index.RolesAt(author));
        Assert.True(index.TryResolve("Hakusensha", CreditRole.Publisher, out var publisher));
        Assert.Equal(CreditRole.Publisher, index.RolesAt(publisher));
    }

    /// <summary>
    /// Being credited as both writer and artist on one title is one work with two roles, not two
    /// works. The dump does this constantly.
    /// </summary>
    [Fact]
    public void One_person_credited_twice_on_a_series_holds_it_once()
    {
        _db.AddSeries(1, "Berserk", authorsJson: """["Kentaro Miura"]""", artistsJson: """["Kentaro Miura"]""");

        var index = Build();

        Assert.True(index.TryResolve("Kentaro Miura", CreditRole.Any, out var id));
        Assert.Equal(1, index.WorkCountOf(id));
        Assert.Equal(1, index.WorkCountOf(id, CreditRole.Artist));
    }

    [Fact]
    public void Spellings_differing_only_in_word_order_or_punctuation_merge()
    {
        _db.AddSeries(1, "A", authorsJson: """["Junji Ito"]""");
        _db.AddSeries(2, "B", authorsJson: """["ITO, Junji"]""");
        _db.AddSeries(3, "C", authorsJson: """["junji  ito"]""");

        var index = Build();

        Assert.Equal(1, index.NameCount);
        Assert.True(index.TryResolve("Junji Ito", CreditRole.Author, out var id));
        Assert.Equal(3, index.WorkCountOf(id));
    }

    /// <summary>
    /// "Junji Itou" and "Junji Ito" are one person in two romanizations, and the dump splits their
    /// bibliography across both. Opening Berserk and clicking "MIURA Kentaro" used to land on a
    /// Kentarou Miura page that did not list Berserk, for exactly this reason.
    /// </summary>
    [Fact]
    public void Romanization_variants_merge_into_one_creator()
    {
        _db.AddSeries(1, "Rare", authorsJson: """["Ito Junji"]""");
        for (var i = 2; i <= 6; i++)
        {
            _db.AddSeries(i, $"Common {i}", authorsJson: """["Junji Itou"]""");
        }

        var index = Build();

        Assert.Equal(1, index.NameCount);
        Assert.True(index.TryResolve("Junji Ito", CreditRole.Author, out var id));
        Assert.Equal(6, index.WorkCountOf(id));
        // The display name is the spelling the catalogue mostly uses, not whichever came first.
        Assert.Equal("Junji Itou", index.NameAt(id));
    }

    [Fact]
    public void A_doubled_consonant_still_separates_two_names()
    {
        _db.AddSeries(1, "A", authorsJson: """["Ippo Tanaka"]""");
        _db.AddSeries(2, "B", authorsJson: """["Ipo Tanaka"]""");

        Assert.Equal(2, Build().NameCount);
    }

    [Fact]
    public void Works_are_ordered_by_popularity_with_unknown_last()
    {
        _db.AddSeries(1, "Obscure", authorsJson: """["A"]""", popularity: 90_000);
        _db.AddSeries(2, "Unranked", authorsJson: """["A"]""");
        _db.AddSeries(3, "Famous", authorsJson: """["A"]""", popularity: 12);

        var index = Build();

        Assert.True(index.TryResolve("A", CreditRole.Author, out var id));
        Assert.Equal([3L, 1L, 2L], index.WorksOf(id));
    }

    /// <summary>
    /// Publisher entries are objects in the dump, occasionally bare strings. Both are names.
    /// </summary>
    [Fact]
    public void Publishers_are_read_from_objects_and_from_bare_strings()
    {
        _db.AddSeries(1, "A", publishersJson: """[{"name": "Shueisha", "type": "Original"}, "Viz Media"]""");

        var index = Build();

        Assert.True(index.TryResolve("Shueisha", CreditRole.Publisher, out _));
        Assert.True(index.TryResolve("Viz Media", CreditRole.Publisher, out _));
    }

    /// <summary>
    /// SQLite's <c>json_each</c> throws "malformed JSON" on some rows of these columns, which is why
    /// the index parses them in C#. One unreadable column must cost that column, not the row and not
    /// the build.
    /// </summary>
    [Fact]
    public void A_malformed_json_column_loses_only_that_column()
    {
        _db.AddSeries(1, "A", authorsJson: "{ this is not json", artistsJson: """["Real Artist"]""");
        _db.AddSeries(2, "B", authorsJson: """["Real Author"]""");

        var index = Build();

        Assert.True(index.TryResolve("Real Artist", CreditRole.Artist, out _));
        Assert.True(index.TryResolve("Real Author", CreditRole.Author, out _));
    }

    [Fact]
    public void Merged_and_novel_rows_are_skipped()
    {
        _db.AddSeries(1, "Merged", state: "merged", mergedWith: "2", authorsJson: """["Ghost"]""");
        _db.AddSeries(2, "Novel", type: "novel", authorsJson: """["Novelist"]""");
        _db.AddSeries(3, "Manga", authorsJson: """["Real"]""");

        var index = Build();

        Assert.False(index.TryResolve("Ghost", CreditRole.Any, out _));
        Assert.False(index.TryResolve("Novelist", CreditRole.Any, out _));
        Assert.True(index.TryResolve("Real", CreditRole.Any, out _));
    }

    [Fact]
    public void A_role_the_name_does_not_hold_does_not_resolve()
    {
        _db.AddSeries(1, "A", publishersJson: """["Shueisha"]""");

        var index = Build();

        Assert.True(index.TryResolve("Shueisha", CreditRole.Publisher, out _));
        Assert.False(index.TryResolve("Shueisha", CreditRole.Author, out _));
    }

    [Fact]
    public void TryResolveFuzzy_tolerates_one_edit_but_only_when_asked()
    {
        _db.AddSeries(1, "A", authorsJson: """["Naoki Urasawa"]""");

        var index = Build();

        Assert.False(index.TryResolve("Naoki Urasaw", CreditRole.Author, out _));
        Assert.True(index.TryResolveFuzzy("Naoki Urasaw", CreditRole.Author, 1, out _));
        Assert.False(index.TryResolveFuzzy("Naoki Urasaw", CreditRole.Author, 0, out _));
    }

    [Fact]
    public void Suggest_puts_the_prolific_name_first()
    {
        _db.AddSeries(1, "A", authorsJson: """["Junji"]""");
        for (var i = 2; i <= 5; i++)
        {
            _db.AddSeries(i, $"B{i}", authorsJson: """["Junji Yamamoto"]""");
        }

        var index = Build();

        var hits = index.Suggest("junji", CreditRole.Any, 5);
        Assert.Equal("Junji Yamamoto", index.NameAt(hits[0].NameId));
    }

    // --- the implicit channel -------------------------------------------------------------

    /// <summary>
    /// The case the whole longest-run rule exists for. "uzumaki" is a title word and "junji ito" is
    /// a name; a whole-query match rule finds neither.
    /// </summary>
    [Fact]
    public void The_channel_picks_the_longest_run_that_names_somebody()
    {
        _db.AddSeries(1, "Uzumaki", authorsJson: """["Junji Ito"]""");

        var index = Build();

        var match = CreditChannel.Select(
            CatalogueText.Tokenize("uzumaki junji ito"), index, maxWorks: 400, minRunChars: 4, minRunTokens: 2);

        Assert.NotNull(match);
        Assert.Equal("Junji Ito", index.NameAt(match!.Value.NameId));
    }

    /// <summary>
    /// The dump credits people whose whole name is one ordinary word: a creator called "Akira" with
    /// 33 works, one called "Winter". Allowing a single-token run inside a longer query hands their
    /// bibliography to any query containing the word.
    /// </summary>
    [Fact]
    public void The_channel_ignores_a_one_word_name_inside_a_longer_query()
    {
        _db.AddSeries(1, "Something Else", authorsJson: """["Akira"]""");

        var index = Build();

        Assert.Null(CreditChannel.Select(
            CatalogueText.Tokenize("akira otomo"), index, maxWorks: 400, minRunChars: 4, minRunTokens: 2));

        // A one-word query is still allowed to name somebody: one word is all it gave us.
        Assert.NotNull(CreditChannel.Select(
            CatalogueText.Tokenize("akira"), index, maxWorks: 400, minRunChars: 4, minRunTokens: 2));
    }

    /// <summary>
    /// Publishers hold five figures of credits. Letting a bare "shueisha" fire the channel folds
    /// twelve thousand rows into the fusion at once; someone who wants that list types
    /// <c>studio:shueisha</c>, which is a filter and not subject to this.
    /// </summary>
    [Fact]
    public void The_channel_ignores_a_name_credited_on_too_much()
    {
        for (var i = 1; i <= 6; i++)
        {
            _db.AddSeries(i, $"S{i}", authorsJson: """["Prolific Person"]""");
        }

        var index = Build();
        var tokens = CatalogueText.Tokenize("prolific person");

        Assert.NotNull(CreditChannel.Select(tokens, index, maxWorks: 6, minRunChars: 4, minRunTokens: 2));
        Assert.Null(CreditChannel.Select(tokens, index, maxWorks: 5, minRunChars: 4, minRunTokens: 2));
    }

    [Fact]
    public void The_channel_only_ever_matches_creators_not_publishers()
    {
        _db.AddSeries(1, "A", publishersJson: """["Small Press"]""");

        var index = Build();

        Assert.Null(CreditChannel.Select(
            CatalogueText.Tokenize("small press"), index, maxWorks: 400, minRunChars: 4, minRunTokens: 2));
    }

    // --- resolution -----------------------------------------------------------------------

    [Fact]
    public void Terms_of_one_role_union_and_terms_of_different_roles_intersect()
    {
        _db.AddSeries(1, "A", authorsJson: """["Alice"]""", publishersJson: """["Press"]""");
        _db.AddSeries(2, "B", authorsJson: """["Bob"]""");
        _db.AddSeries(3, "C", authorsJson: """["Alice"]""");

        var index = Build();

        var union = CreditResolver.Resolve(
            CatalogueQuery.Parse("author:\"Alice\" author:\"Bob\""), index, CatalogueOptions.Default);
        Assert.Equal([1L, 2L, 3L], union.SeriesIds!.Order());

        var intersect = CreditResolver.Resolve(
            CatalogueQuery.Parse("author:\"Alice\" studio:\"Press\""), index, CatalogueOptions.Default);
        Assert.Equal([1L], intersect.SeriesIds!);
    }

    /// <summary>
    /// Dropping an unresolvable name would answer a search for one author's work with the whole
    /// catalogue, which reads as the filter having been ignored, because it was.
    /// </summary>
    [Fact]
    public void An_unknown_name_makes_the_query_impossible_rather_than_unfiltered()
    {
        _db.AddSeries(1, "A", authorsJson: """["Alice"]""");

        var resolution = CreditResolver.Resolve(
            CatalogueQuery.Parse("author:\"Nobody At All\""), Build(), CatalogueOptions.Default);

        Assert.True(resolution.Impossible);
        Assert.NotNull(resolution.SeriesIds);
        Assert.Empty(resolution.SeriesIds!);
    }

    /// <summary>An unquoted value that overshoots gives the extra words back as search text.</summary>
    [Fact]
    public void Words_that_were_not_part_of_the_name_come_back_as_free_text()
    {
        _db.AddSeries(1, "Uzumaki", authorsJson: """["Junji Ito"]""");

        var resolution = CreditResolver.Resolve(
            CatalogueQuery.Parse("author:junji ito uzumaki"), Build(), CatalogueOptions.Default);

        Assert.Equal([1L], resolution.SeriesIds!);
        Assert.Equal("uzumaki", resolution.ExtraFreeText);
    }

    [Fact]
    public void A_query_with_no_credits_restricts_nothing()
    {
        var resolution = CreditResolver.Resolve(
            CatalogueQuery.Parse("berserk"), Build(), CatalogueOptions.Default);

        Assert.False(resolution.Restricts);
        Assert.Null(resolution.SeriesIds);
    }
}
