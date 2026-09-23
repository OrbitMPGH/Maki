using Maki.Core.Configuration;

namespace Maki.Core.Tests;

/// <summary>
/// <see cref="CustomRailSpec.Normalize"/>: the fallbacks that keep a hand-rolled or stale spec from
/// producing something a rail can't actually resolve.
/// </summary>
public class CustomRailSpecTests
{
    [Fact]
    public void Normalize_falls_back_to_catalogue_for_an_unknown_source()
    {
        var spec = new CustomRailSpec(Source: "nonsense").Normalize();

        Assert.Equal(CustomRailSources.Catalogue, spec.Source);
    }

    [Fact]
    public void Normalize_falls_back_to_the_sources_default_sort_for_a_bad_sort()
    {
        var spec = new CustomRailSpec(Source: CustomRailSources.Library, Sort: "not-a-sort").Normalize();

        Assert.Equal(CustomRailSorts.DefaultFor(CustomRailSources.Library), spec.Sort);
        Assert.Equal(CustomRailSorts.Added, spec.Sort);
    }

    [Fact]
    public void Normalize_gives_recommendations_a_null_sort_regardless_of_what_was_asked()
    {
        // The recommender has no sort of its own — its order is the ranking — so nothing in
        // CustomRailSorts.For(Recommendations) exists for a requested value to match.
        var spec = new CustomRailSpec(Source: CustomRailSources.Recommendations, Sort: "popular").Normalize();

        Assert.Null(spec.Sort);
    }

    [Fact]
    public void Normalize_keeps_seeds_obscurity_and_diversity_only_for_recommendations()
    {
        var seeds = new[] { new RecommendationSeed(1) };

        var catalogue = new CustomRailSpec(
            Source: CustomRailSources.Catalogue, Seeds: seeds, Obscurity: 0.5, Diversity: 0.5).Normalize();
        Assert.Null(catalogue.Seeds);
        Assert.Equal(0, catalogue.Obscurity);
        Assert.Equal(0, catalogue.Diversity);

        var library = new CustomRailSpec(
            Source: CustomRailSources.Library, Seeds: seeds, Obscurity: 0.5, Diversity: 0.5).Normalize();
        Assert.Null(library.Seeds);
        Assert.Equal(0, library.Obscurity);
        Assert.Equal(0, library.Diversity);

        var recs = new CustomRailSpec(
            Source: CustomRailSources.Recommendations, Seeds: seeds, Obscurity: 0.5, Diversity: 0.5).Normalize();
        Assert.Equal([1], recs.Seeds!.Select(s => s.Id));
        Assert.Equal(0.5, recs.Obscurity);
        Assert.Equal(0.5, recs.Diversity);
    }

    [Fact]
    public void Normalize_clamps_obscurity_and_diversity_and_zeroes_non_finite_values()
    {
        var high = new CustomRailSpec(Source: CustomRailSources.Recommendations, Obscurity: 5, Diversity: 5).Normalize();
        Assert.Equal(1, high.Obscurity);
        Assert.Equal(1, high.Diversity);

        var low = new CustomRailSpec(Source: CustomRailSources.Recommendations, Obscurity: -5, Diversity: -5).Normalize();
        Assert.Equal(-1, low.Obscurity);
        Assert.Equal(0, low.Diversity);

        var nonFinite = new CustomRailSpec(
            Source: CustomRailSources.Recommendations,
            Obscurity: double.NaN,
            Diversity: double.PositiveInfinity).Normalize();
        Assert.Equal(0, nonFinite.Obscurity);
        Assert.Equal(0, nonFinite.Diversity);
    }

    [Fact]
    public void Normalize_deduplicates_drops_invalid_ids_and_caps_seeds_at_one_hundred()
    {
        var seeds = Enumerable.Range(1, 150).Select(i => new RecommendationSeed(i))
            .Append(new RecommendationSeed(1)) // duplicate
            .Append(new RecommendationSeed(0)) // invalid
            .ToList();

        var spec = new CustomRailSpec(Source: CustomRailSources.Recommendations, Seeds: seeds).Normalize();

        Assert.Equal(100, spec.Seeds!.Count);
        Assert.DoesNotContain(spec.Seeds, s => s.Id <= 0);
        Assert.Equal(spec.Seeds.Select(s => s.Id), spec.Seeds.Select(s => s.Id).Distinct());
    }

    [Fact]
    public void Normalize_keeps_excludeOwned_only_for_catalogue()
    {
        Assert.True(new CustomRailSpec(Source: CustomRailSources.Catalogue, ExcludeOwned: true).Normalize().ExcludeOwned);
        Assert.False(new CustomRailSpec(Source: CustomRailSources.Catalogue, ExcludeOwned: false).Normalize().ExcludeOwned);
        Assert.False(new CustomRailSpec(Source: CustomRailSources.Library, ExcludeOwned: true).Normalize().ExcludeOwned);
        Assert.False(new CustomRailSpec(Source: CustomRailSources.Recommendations, ExcludeOwned: true).Normalize().ExcludeOwned);
    }

    [Fact]
    public void Serialized_json_uses_camelCase_property_names()
    {
        var json = CustomRailSpec.Serialize(new CustomRailSpec(Source: CustomRailSources.Recommendations));

        Assert.Contains("\"source\"", json);
        Assert.Contains("\"sort\"", json);
        Assert.Contains("\"excludeOwned\"", json);
        Assert.Contains("\"seeds\"", json);
        Assert.Contains("\"obscurity\"", json);
        Assert.Contains("\"diversity\"", json);
        Assert.Contains("\"filters\"", json);
    }

    [Fact]
    public void Parse_of_garbage_or_blank_json_returns_the_normalized_default()
    {
        var expected = CustomRailSpec.Empty.Normalize();

        Assert.Equal(expected, CustomRailSpec.Parse(null));
        Assert.Equal(expected, CustomRailSpec.Parse("   "));
        Assert.Equal(expected, CustomRailSpec.Parse("{ not json"));
    }
}
