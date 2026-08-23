using Maki.Metadata.Catalogue;
using Maki.Metadata.MangaBaka;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Metadata.Tests;

public class FuzzyTermIndexTests : IDisposable
{
    private readonly DumpDbBuilder _db = new();

    public void Dispose() => _db.Dispose();

    private FuzzyTermIndex Build(bool withSearchIndex = true)
    {
        if (withSearchIndex)
        {
            _db.BuildSearchIndex();
        }

        using var conn = new SqliteConnection($"Data Source={_db.Path};Mode=ReadOnly;Pooling=False");
        conn.Open();
        return FuzzyTermIndex.Build(conn, MangaBakaDumpService.SearchTableName, NullLogger.Instance);
    }

    [Fact]
    public void Finds_a_single_substitution()
    {
        _db.AddSeries(1, "Berserk");

        var expansions = Build().Expand("berserck", FuzzyOptions.Default);

        Assert.Contains(expansions, e => e.Term == "berserk" && e.Distance == 1);
    }

    [Fact]
    public void Finds_a_transposition_as_one_edit()
    {
        _db.AddSeries(1, "Berserk");

        var expansions = Build().Expand("bersrek", FuzzyOptions.Default);

        Assert.Contains(expansions, e => e.Term == "berserk" && e.Distance == 1);
    }

    [Fact]
    public void Never_offers_the_token_itself()
    {
        _db.AddSeries(1, "Berserk");

        Assert.DoesNotContain(Build().Expand("berserk", FuzzyOptions.Default), e => e.Term == "berserk");
    }

    /// <summary>
    /// The floor is low (three) because <see cref="FuzzyOptions.RescueBelow"/> is what keeps a loose
    /// expansion off queries that already work. Below it, nothing expands at all.
    /// </summary>
    [Fact]
    public void Ignores_a_token_below_the_length_floor()
    {
        _db.AddSeries(1, "Gantz");
        _db.AddSeries(2, "Go");

        var index = Build();

        Assert.Empty(index.Expand("g", FuzzyOptions.Default));
        Assert.Empty(index.Expand("go", FuzzyOptions.Default));
        Assert.NotEmpty(index.Expand("gantx", FuzzyOptions.Default));
    }

    /// <summary>
    /// unicode61 does not word-segment CJK, so a whole title arrives as one token and an edit away
    /// from it is a different phrase rather than a typo. A length gate cannot catch this.
    /// </summary>
    [Theory]
    [InlineData("進撃の巨人")]
    [InlineData("しんげきのきょじん")]
    [InlineData("берсерк")]
    public void Ignores_a_token_that_is_not_ascii(string token)
    {
        _db.AddSeries(1, "進撃の巨大", nativeTitle: "берсерка");

        Assert.Empty(Build().Expand(token, FuzzyOptions.Default));
    }

    /// <summary>
    /// One expansion into a term that is in most of the catalogue turns a rescue into a popularity
    /// chart. Against the shipped index the head of the dictionary is "no" in 286k titles.
    /// </summary>
    [Fact]
    public void Never_offers_a_term_that_is_already_everywhere()
    {
        _db.AddSeries(1, "Titan Rising");
        _db.AddSeries(2, "Titan Falling");
        _db.AddSeries(3, "Titan Again");

        var index = Build();
        var permissive = FuzzyOptions.Default with { MaxTermDocFrequency = 100 };
        var strict = FuzzyOptions.Default with { MaxTermDocFrequency = 2 };

        Assert.Contains(index.Expand("titen", permissive), e => e.Term == "titan");
        Assert.DoesNotContain(index.Expand("titen", strict), e => e.Term == "titan");
    }

    /// <summary>
    /// Document frequency alone cannot separate a typo from a real word. Against the shipped index
    /// "kaisan" appears in 10 titles and is a misspelling of "kaisen" (1,639), while "vinland"
    /// appears in 12 and is spelled correctly. The gap between them is what separates the two.
    /// </summary>
    [Fact]
    public void Only_offers_a_spelling_that_dominates_the_one_typed()
    {
        // "inland" and "vinland" each appear once, so neither dominates the other.
        _db.AddSeries(1, "Vinland Saga");
        _db.AddSeries(2, "Inland Empire");

        Assert.Empty(Build().Expand("vinland", FuzzyOptions.Default));
    }

    [Fact]
    public void Respects_the_expansion_cap()
    {
        foreach (var (id, title) in new[] { (1, "kaisen"), (2, "kaikan"), (3, "kaidan"), (4, "kaigan"), (5, "kaasan") }
                     .Select((t, i) => (i + 1, t.Item2)))
        {
            _db.AddSeries(id, title);
        }

        var expansions = Build().Expand("kaisan", FuzzyOptions.Default with { MaxExpansionsPerToken = 2 });

        Assert.Equal(2, expansions.Count);
    }

    [Fact]
    public void Expansions_come_back_closest_first()
    {
        _db.AddSeries(1, "berserk");
        _db.AddSeries(2, "berserker");

        var expansions = Build().Expand("berserck", FuzzyOptions.Default);

        Assert.True(expansions[0].Distance <= expansions[^1].Distance);
        Assert.Equal("berserk", expansions[0].Term);
    }

    /// <summary>
    /// A dump with no title index degrades to no typo tolerance. Throwing here would take out
    /// ordinary search with it.
    /// </summary>
    [Fact]
    public void A_dump_with_no_search_table_yields_an_empty_index()
    {
        _db.AddSeries(1, "Berserk");

        var index = Build(withSearchIndex: false);

        Assert.True(index.IsEmpty);
        Assert.Empty(index.Expand("berserck", FuzzyOptions.Default));
    }

    [Fact]
    public void Disabled_options_expand_nothing()
    {
        _db.AddSeries(1, "Berserk");

        Assert.Empty(Build().Expand("berserck", FuzzyOptions.Default with { Enabled = false }));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 1)]
    [InlineData(5, 1)]
    [InlineData(6, 2)]
    [InlineData(12, 2)]
    public void The_edit_budget_follows_the_length_ladder(int length, int expected) =>
        Assert.Equal(expected, FuzzyOptions.Default.BudgetFor(length));
}
