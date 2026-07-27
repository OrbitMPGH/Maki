using Maki.Core.Configuration;

namespace Maki.Core.Tests;

/// <summary>
/// The Home layout blob. Most of these pin <see cref="HomeLayoutSpec.Merge"/>, which is what keeps
/// a stored layout working across a release that adds or removes a section.
/// </summary>
public class HomeLayoutTests
{
    [Fact]
    public void Default_has_every_section_enabled_in_shipping_order()
    {
        var spec = HomeLayoutSpec.Default;

        Assert.True(spec.Enabled);
        Assert.Equal(HomeSections.All, spec.Sections!.Select(s => s.Key));
        Assert.All(spec.Sections!, s => Assert.True(s.Enabled));
    }

    [Fact]
    public void Parse_falls_back_to_default_for_blank_and_broken_json()
    {
        Assert.Equal(HomeSections.All.Length, HomeLayoutSpec.Parse(null).Sections!.Count);
        Assert.Equal(HomeSections.All.Length, HomeLayoutSpec.Parse("  ").Sections!.Count);
        Assert.Equal(HomeSections.All.Length, HomeLayoutSpec.Parse("{ not json").Sections!.Count);
        Assert.True(HomeLayoutSpec.Parse("{ not json").Enabled);
    }

    [Fact]
    public void Merge_appends_sections_the_stored_layout_has_never_seen()
    {
        // A layout written before the newer sections existed, deliberately reordered.
        var stored = new HomeLayoutSpec(true, [
            new HomeSection(HomeSections.Stats),
            new HomeSection(HomeSections.ContinueReading, Enabled: false),
        ]);

        var merged = stored.Merge();

        // The user's own order survives at the front...
        Assert.Equal(HomeSections.Stats, merged.Sections![0].Key);
        Assert.Equal(HomeSections.ContinueReading, merged.Sections[1].Key);
        Assert.False(merged.Sections[1].Enabled);
        // ...and everything new lands after it, on, rather than jumping to its canonical slot.
        Assert.Equal(HomeSections.All.Length, merged.Sections.Count);
        Assert.All(merged.Sections.Skip(2), s => Assert.True(s.Enabled));
    }

    [Fact]
    public void Merge_drops_keys_this_build_does_not_know()
    {
        var stored = new HomeLayoutSpec(true, [
            new HomeSection("a-section-from-the-future"),
            new HomeSection(HomeSections.Popular),
        ]);

        var merged = stored.Merge();

        Assert.DoesNotContain(merged.Sections!, s => s.Key == "a-section-from-the-future");
        Assert.Equal(HomeSections.Popular, merged.Sections![0].Key);
        Assert.Equal(HomeSections.All.Length, merged.Sections.Count);
    }

    [Fact]
    public void Merge_collapses_duplicates_to_the_first_occurrence()
    {
        var stored = new HomeLayoutSpec(true, [
            new HomeSection(HomeSections.Popular, Enabled: false),
            new HomeSection(HomeSections.Popular, Enabled: true),
        ]);

        var merged = stored.Merge();

        Assert.Equal(HomeSections.All.Length, merged.Sections!.Count);
        Assert.False(merged.Sections.Single(s => s.Key == HomeSections.Popular).Enabled);
    }

    [Fact]
    public void Round_trips_order_disabled_flags_and_the_master_switch()
    {
        var spec = new HomeLayoutSpec(false, [
            new HomeSection(HomeSections.RecentlyAdded),
            new HomeSection(HomeSections.ContinueReading, Enabled: false),
        ]);

        var parsed = HomeLayoutSpec.Parse(HomeLayoutSpec.Serialize(spec));

        Assert.False(parsed.Enabled);
        Assert.Equal(HomeSections.RecentlyAdded, parsed.Sections![0].Key);
        Assert.False(parsed.Sections[1].Enabled);
    }

    [Fact]
    public void Serialized_json_is_camel_case()
    {
        // The frontend reads this blob directly; a casing change would silently yield defaults.
        Assert.Contains("\"enabled\"", HomeLayoutSpec.Serialize(HomeLayoutSpec.Default));
        Assert.Contains("\"sections\"", HomeLayoutSpec.Serialize(HomeLayoutSpec.Default));
        Assert.Contains("\"key\"", HomeLayoutSpec.Serialize(HomeLayoutSpec.Default));
    }
}
