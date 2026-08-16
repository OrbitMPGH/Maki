using Maki.Core.Configuration;

namespace Maki.Core.Tests;

/// <summary>
/// The saved Discover-search filter blob. Same shape of concern as
/// <see cref="RecommendationDefaultsTests"/>: <see cref="SearchDefaultsSpec.IsEmpty"/> is what lets
/// one button both set and clear a default, and the clamping keeps a hand-rolled request from
/// parking junk in the settings table.
/// </summary>
public class SearchDefaultsTests
{
    [Fact]
    public void Empty_spec_reads_as_no_default()
    {
        Assert.True(SearchDefaultsSpec.Empty.IsEmpty);
        Assert.True(SearchDefaultsSpec.Parse(null).IsEmpty);
        Assert.True(SearchDefaultsSpec.Parse("  ").IsEmpty);
        Assert.True(SearchDefaultsSpec.Parse("{ not json").IsEmpty);
    }

    [Fact]
    public void Any_single_constraint_makes_it_non_empty()
    {
        Assert.False(new SearchDefaultsSpec(YearMin: 2000).IsEmpty);
        Assert.False(new SearchDefaultsSpec(Genres: ["Action"]).IsEmpty);
        Assert.False(new SearchDefaultsSpec(Tags: ["Childhood Friends"]).IsEmpty);
        Assert.False(new SearchDefaultsSpec(MinRating: 70).IsEmpty);
        Assert.False(new SearchDefaultsSpec(MaxChapters: 300).IsEmpty);
    }

    [Fact]
    public void Normalize_clamps_the_rating_and_drops_junk()
    {
        var spec = new SearchDefaultsSpec(Genres: ["Action", "  ", ""], MinRating: 180).Normalize();

        Assert.Equal(["Action"], spec.Genres!);
        Assert.Equal(100, spec.MinRating);
    }

    [Fact]
    public void Empty_lists_normalize_to_null_so_they_do_not_count_as_a_default()
    {
        var spec = new SearchDefaultsSpec(Genres: [], Tags: [" "], Types: []).Normalize();

        Assert.Null(spec.Genres);
        Assert.Null(spec.Tags);
        Assert.Null(spec.Types);
        Assert.True(spec.IsEmpty);
    }

    [Fact]
    public void Round_trips_through_the_stored_blob()
    {
        var spec = new SearchDefaultsSpec(
            YearMin: 1990,
            YearMax: 2010,
            Types: ["manga"],
            Statuses: ["completed"],
            Genres: ["Romance"],
            Tags: ["Childhood Friends"],
            MinChapters: 10,
            MaxChapters: 300,
            MinRating: 75);

        var json = SearchDefaultsSpec.Serialize(spec);
        var parsed = SearchDefaultsSpec.Parse(json);

        // Compared through the blob, not with Assert.Equal: the record's synthesized equality is
        // reference equality for its list members, so two identical specs are never "equal".
        Assert.Equal(json, SearchDefaultsSpec.Serialize(parsed));
        Assert.Equal(["Childhood Friends"], parsed.Tags!);
        Assert.Equal(75, parsed.MinRating);
    }

    /// <summary>
    /// The blob is written camelCase but read case-insensitively, so a spec stored by a build that
    /// serialized PascalCase still applies rather than silently degrading to "no default".
    /// </summary>
    [Fact]
    public void Reads_a_pascal_cased_blob()
    {
        var spec = SearchDefaultsSpec.Parse("""{"Genres":["Horror"],"MinRating":80}""");

        Assert.Equal(["Horror"], spec.Genres!);
        Assert.Equal(80, spec.MinRating);
    }

    /// <summary>
    /// The two saved-default blobs are separate settings on purpose, and this is the property that
    /// matters: a search spec never carries the recommender's seeds or dials, so saving one panel
    /// cannot rewrite the other's.
    /// </summary>
    [Fact]
    public void Ignores_the_recommender_only_fields_of_the_other_spec()
    {
        var spec = SearchDefaultsSpec.Parse(
            """{"genres":["Horror"],"obscurity":0.5,"diversity":1,"seeds":[{"id":7}]}""");

        Assert.Equal(["Horror"], spec.Genres!);
        Assert.False(spec.IsEmpty);
        Assert.Equal("""{"yearMin":null,"yearMax":null,"types":null,"statuses":null,"genres":["Horror"],"tags":null,"minChapters":null,"maxChapters":null,"minRating":null,"contentRatings":null}""",
            SearchDefaultsSpec.Serialize(spec));
    }
}
