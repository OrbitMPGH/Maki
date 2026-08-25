using Maki.Metadata.Catalogue;

namespace Maki.Metadata.Tests;

public class CatalogueQueryTests
{
    [Fact]
    public void Plain_text_carries_no_credits()
    {
        var parsed = CatalogueQuery.Parse("girls camping near mount fuji");

        Assert.False(parsed.HasCredits);
        Assert.Equal("girls camping near mount fuji", parsed.FreeText);
    }

    /// <summary>
    /// The reason this is a hand-rolled scan and not a split on colons. Half the catalogue has a
    /// subtitle, and none of those are field syntax.
    /// </summary>
    [Theory]
    [InlineData("Kaguya-sama: Love is War")]
    [InlineData("Attack on Titan: Before the Fall")]
    [InlineData("3:15")]
    public void A_subtitle_is_not_a_keyword(string title)
    {
        var parsed = CatalogueQuery.Parse(title);

        Assert.False(parsed.HasCredits);
        Assert.Equal(title, parsed.FreeText);
    }

    [Fact]
    public void A_quoted_value_is_exactly_what_is_inside_the_quotes()
    {
        var parsed = CatalogueQuery.Parse("author:\"Junji Ito\" uzumaki");

        var term = Assert.Single(parsed.Credits);
        Assert.Equal(CreditRole.Author, term.Roles);
        Assert.Equal("Junji Ito", term.Name);
        Assert.Equal("uzumaki", parsed.FreeText);
    }

    /// <summary>
    /// An unquoted value runs to the next keyword, because nobody types the quotes and
    /// <c>author:junji</c> plus a loose "ito" finds a different person entirely. Anything in there
    /// that turns out not to be a name comes back through <see cref="CreditResolver"/>.
    /// </summary>
    [Fact]
    public void An_unquoted_value_runs_to_the_next_keyword()
    {
        var parsed = CatalogueQuery.Parse("author:junji ito studio:shueisha");

        Assert.Equal(2, parsed.Credits.Count);
        Assert.Equal("junji ito", parsed.Credits[0].Name);
        Assert.Equal(CreditRole.Author, parsed.Credits[0].Roles);
        Assert.Equal("shueisha", parsed.Credits[1].Name);
        Assert.Equal(CreditRole.Publisher, parsed.Credits[1].Roles);
        Assert.Equal(string.Empty, parsed.FreeText);
    }

    [Theory]
    [InlineData("author:x", CreditRole.Author)]
    [InlineData("ARTIST:x", CreditRole.Artist)]
    [InlineData("studio:x", CreditRole.Publisher)]
    [InlineData("publisher:x", CreditRole.Publisher)]
    [InlineData("by:x", CreditRole.Creator)]
    public void Every_keyword_maps_to_its_role(string query, CreditRole expected) =>
        Assert.Equal(expected, Assert.Single(CatalogueQuery.Parse(query).Credits).Roles);

    [Fact]
    public void A_keyword_only_counts_at_the_start_of_a_word()
    {
        var parsed = CatalogueQuery.Parse("coauthor:someone");

        Assert.False(parsed.HasCredits);
        Assert.Equal("coauthor:someone", parsed.FreeText);
    }

    [Fact]
    public void Repeated_keywords_are_all_kept()
    {
        var parsed = CatalogueQuery.Parse("author:\"A\" author:\"B\"");

        Assert.Equal(2, parsed.Credits.Count);
        Assert.All(parsed.Credits, c => Assert.Equal(CreditRole.Author, c.Roles));
    }

    [Fact]
    public void An_empty_value_is_dropped()
    {
        var parsed = CatalogueQuery.Parse("author: berserk");

        // "berserk" follows the colon, so the unquoted value takes it; there is no way to tell that
        // apart from someone typing the keyword and then their query, and treating it as the name
        // is what the resolver can recover from.
        Assert.Equal("berserk", Assert.Single(parsed.Credits).Name);
    }

    [Fact]
    public void A_trailing_keyword_with_nothing_after_it_is_dropped()
    {
        var parsed = CatalogueQuery.Parse("berserk author:");

        Assert.False(parsed.HasCredits);
        Assert.Equal("berserk", parsed.FreeText);
    }

    [Fact]
    public void An_unbalanced_quote_takes_the_rest_of_the_box()
    {
        // Somebody mid-type. Better to search for the partial name than to drop the term.
        var parsed = CatalogueQuery.Parse("author:\"Junji It");

        Assert.Equal("Junji It", Assert.Single(parsed.Credits).Name);
    }
}
