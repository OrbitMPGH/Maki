using Maki.Core.Configuration;

namespace Maki.Core.Tests;

/// <summary>
/// The saved Recommended-panel blob. The interesting parts are <see cref="RecommendationDefaultsSpec.IsEmpty"/>,
/// which is what lets one button both set and clear a default, and the clamping that keeps a
/// hand-rolled request from parking junk in the settings table.
/// </summary>
public class RecommendationDefaultsTests
{
    [Fact]
    public void Empty_spec_reads_as_no_default()
    {
        Assert.True(RecommendationDefaultsSpec.Empty.IsEmpty);
        Assert.True(RecommendationDefaultsSpec.Parse(null).IsEmpty);
        Assert.True(RecommendationDefaultsSpec.Parse("  ").IsEmpty);
        Assert.True(RecommendationDefaultsSpec.Parse("{ not json").IsEmpty);
    }

    [Fact]
    public void Any_single_constraint_makes_it_non_empty()
    {
        Assert.False(new RecommendationDefaultsSpec(YearMin: 2000).IsEmpty);
        Assert.False(new RecommendationDefaultsSpec(Genres: ["Action"]).IsEmpty);
        Assert.False(new RecommendationDefaultsSpec(MinRating: 70).IsEmpty);
        Assert.False(new RecommendationDefaultsSpec(Obscurity: 0.5).IsEmpty);
        Assert.False(new RecommendationDefaultsSpec(Seeds: [new RecommendationSeed(1, "Berserk")]).IsEmpty);
    }

    [Fact]
    public void Normalize_clamps_the_dials_and_drops_junk()
    {
        var spec = new RecommendationDefaultsSpec(
            Seeds: [new RecommendationSeed(12345, "Berserk"), new RecommendationSeed(0, "no id")],
            Genres: ["Action", "  ", ""],
            MinRating: 180,
            Obscurity: 5).Normalize();

        Assert.Equal([12345], spec.Seeds!.Select(s => s.Id));
        Assert.Equal(["Action"], spec.Genres!);
        Assert.Equal(100, spec.MinRating);
        Assert.Equal(1, spec.Obscurity);
    }

    [Fact]
    public void Empty_lists_normalize_to_null_so_they_do_not_count_as_a_default()
    {
        var spec = new RecommendationDefaultsSpec(Seeds: [], Genres: [], Tags: [" "]).Normalize();

        Assert.Null(spec.Seeds);
        Assert.Null(spec.Genres);
        Assert.Null(spec.Tags);
        Assert.True(spec.IsEmpty);
    }

    [Fact]
    public void Round_trips_through_the_stored_blob()
    {
        var spec = new RecommendationDefaultsSpec(
            Seeds: [new RecommendationSeed(7, "Vagabond")],
            YearMin: 1990,
            YearMax: 2010,
            Types: ["manga"],
            Statuses: ["completed"],
            Genres: ["Action"],
            Tags: ["revenge"],
            MinChapters: 10,
            MaxChapters: 300,
            MinRating: 75,
            Obscurity: -0.5);

        var json = RecommendationDefaultsSpec.Serialize(spec);
        var parsed = RecommendationDefaultsSpec.Parse(json);

        // Compared through the blob, not with Assert.Equal: the record's synthesized equality is
        // reference equality for its list members, so two identical specs are never "equal".
        Assert.Equal(json, RecommendationDefaultsSpec.Serialize(parsed));
        Assert.Equal(["Vagabond"], parsed.Seeds!.Select(s => s.Title));
        Assert.Equal(-0.5, parsed.Obscurity);
    }

    /// <summary>
    /// The blob is written camelCase but read case-insensitively, so a spec stored by a build that
    /// serialized PascalCase still applies rather than silently degrading to "no default".
    /// </summary>
    [Fact]
    public void Reads_a_pascal_cased_blob()
    {
        var spec = RecommendationDefaultsSpec.Parse("""{"Genres":["Horror"],"MinRating":80,"Obscurity":0.25}""");

        Assert.Equal(["Horror"], spec.Genres!);
        Assert.Equal(80, spec.MinRating);
        Assert.Equal(0.25, spec.Obscurity);
    }
}
