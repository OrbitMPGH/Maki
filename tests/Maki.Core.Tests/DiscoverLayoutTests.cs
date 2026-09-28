using Maki.Core.Configuration;

namespace Maki.Core.Tests;

/// <summary>
/// The Discover layout blob. Shares <see cref="PageLayouts.Merge"/> with <see cref="HomeLayoutSpec"/>
/// (see <see cref="HomeLayoutTests"/> for the generic merge behaviour); these pin what is specific to
/// Discover, chiefly <see cref="DiscoverSections.Trending"/> as the rail anchor.
/// </summary>
public class DiscoverLayoutTests
{
    [Fact]
    public void Default_has_every_section_enabled_in_shipping_order()
    {
        var spec = DiscoverLayoutSpec.Default;

        Assert.Equal(DiscoverSections.All, spec.Sections!.Select(s => s.Key));
        Assert.All(spec.Sections!, s => Assert.True(s.Enabled));
    }

    [Fact]
    public void New_rails_are_placed_before_trending_in_the_given_order()
    {
        var merged = new DiscoverLayoutSpec().Merge([1, 2]);

        var keys = merged.Sections!.Select(s => s.Key).ToList();
        var trendingAt = keys.IndexOf(DiscoverSections.Trending);

        Assert.Equal(
            [LayoutRails.Key(1), LayoutRails.Key(2)],
            keys.Where(k => LayoutRails.TryParse(k, out _)));
        Assert.True(keys.IndexOf(LayoutRails.Key(1)) < trendingAt);
        Assert.True(keys.IndexOf(LayoutRails.Key(2)) < trendingAt);
    }

    [Fact]
    public void A_new_rail_goes_after_the_last_existing_rail_even_when_rails_were_moved_elsewhere()
    {
        // The user dragged their one existing rail to the very front, away from Trending.
        var stored = new DiscoverLayoutSpec([
            new PageSection(LayoutRails.Key(1)),
            new PageSection(DiscoverSections.Hero),
            new PageSection(DiscoverSections.Taste),
            new PageSection(DiscoverSections.RecentActivity),
            new PageSection(DiscoverSections.SideInterests),
            new PageSection(DiscoverSections.Cohort),
            new PageSection(DiscoverSections.Trending),
            new PageSection(DiscoverSections.Catalogue),
            new PageSection(DiscoverSections.Genres),
        ]);

        var merged = stored.Merge([1, 2]);

        var keys = merged.Sections!.Select(s => s.Key).ToList();
        // The new rail lands right after rail 1, not back down near Trending.
        Assert.Equal([LayoutRails.Key(1), LayoutRails.Key(2)], keys.Take(2));
    }

    [Fact]
    public void With_no_rails_placed_a_new_rail_goes_right_before_trending_wherever_it_was_moved()
    {
        var stored = new DiscoverLayoutSpec([
            new PageSection(DiscoverSections.Trending),
            new PageSection(DiscoverSections.Hero),
            new PageSection(DiscoverSections.Taste),
            new PageSection(DiscoverSections.RecentActivity),
            new PageSection(DiscoverSections.SideInterests),
            new PageSection(DiscoverSections.Cohort),
            new PageSection(DiscoverSections.Catalogue),
            new PageSection(DiscoverSections.Genres),
        ]);

        var merged = stored.Merge([5]);

        var keys = merged.Sections!.Select(s => s.Key).ToList();
        Assert.Equal(LayoutRails.Key(5), keys[0]);
        Assert.Equal(DiscoverSections.Trending, keys[1]);
    }

    [Fact]
    public void Merge_drops_a_stale_rail_and_an_unknown_section_key()
    {
        var stored = new DiscoverLayoutSpec([
            new PageSection(LayoutRails.Key(5)), // stale: not in the caller's rails below
            new PageSection("a-section-from-the-future"),
            new PageSection(DiscoverSections.Hero),
        ]);

        var merged = stored.Merge([9]);

        var keys = merged.Sections!.Select(s => s.Key).ToList();
        Assert.DoesNotContain(LayoutRails.Key(5), keys);
        Assert.DoesNotContain("a-section-from-the-future", keys);
        Assert.Contains(LayoutRails.Key(9), keys);
    }

    [Fact]
    public void Round_trips_through_parse_and_serialize()
    {
        var spec = new DiscoverLayoutSpec([
            new PageSection(DiscoverSections.Genres),
            new PageSection(DiscoverSections.Hero, Enabled: false),
            new PageSection(LayoutRails.Key(3)),
        ]);

        var json = DiscoverLayoutSpec.Serialize(spec, [3]);
        var parsed = DiscoverLayoutSpec.Parse(json, [3]);

        Assert.Equal(DiscoverSections.Genres, parsed.Sections![0].Key);
        Assert.False(parsed.Sections!.Single(s => s.Key == DiscoverSections.Hero).Enabled);
        Assert.Contains(parsed.Sections!, s => s.Key == LayoutRails.Key(3));
    }

    [Fact]
    public void Parse_falls_back_to_default_for_blank_and_broken_json()
    {
        Assert.Equal(DiscoverSections.All.Length, DiscoverLayoutSpec.Parse(null, []).Sections!.Count);
        Assert.Equal(DiscoverSections.All.Length, DiscoverLayoutSpec.Parse("  ", []).Sections!.Count);
        Assert.Equal(DiscoverSections.All.Length, DiscoverLayoutSpec.Parse("{ not json", []).Sections!.Count);
    }
}
