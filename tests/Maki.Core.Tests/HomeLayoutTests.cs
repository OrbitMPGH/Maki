using Maki.Core.Configuration;

namespace Maki.Core.Tests;

/// <summary>
/// The Home layout blob. Most of these pin <see cref="HomeLayoutSpec.Merge()"/> (via
/// <see cref="PageLayouts.Merge"/>), which is what keeps a stored layout working across a release
/// that adds or removes a section, and <see cref="HomeLayoutSpec.MigrateGlance"/>, which folds the
/// pre-glance stats/progress/toread sections into one.
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
    public void Default_leads_with_glance_holding_every_panel_on()
    {
        var spec = HomeLayoutSpec.Default;

        var glance = spec.Sections![0];
        Assert.Equal(HomeSections.Glance, glance.Key);
        Assert.True(glance.Enabled);
        Assert.Equal(HomeGlancePanels.All, glance.Panels!.Select(p => p.Key));
        Assert.All(glance.Panels!, p => Assert.True(p.Enabled));
    }

    [Fact]
    public void Default_hero_flags_match_the_definitions_defaults()
    {
        var spec = HomeLayoutSpec.Default;

        Assert.True(spec.Sections!.Single(s => s.Key == HomeSections.ContinueReading).Hero);
        Assert.False(spec.Sections!.Single(s => s.Key == HomeSections.JumpBackIn).Hero);
        Assert.All(
            spec.Sections!.Where(s => s.Key is not HomeSections.ContinueReading and not HomeSections.JumpBackIn),
            s => Assert.Null(s.Hero));
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
            new PageSection(HomeSections.Glance),
            new PageSection(HomeSections.ContinueReading, Enabled: false),
        ]);

        var merged = stored.Merge();

        // The user's own order survives at the front...
        Assert.Equal(HomeSections.Glance, merged.Sections![0].Key);
        Assert.Equal(HomeSections.ContinueReading, merged.Sections[1].Key);
        Assert.False(merged.Sections[1].Enabled);
        // ...and everything new lands after it, on, rather than jumping to its canonical slot.
        Assert.Equal(HomeSections.All.Length, merged.Sections.Count);
        Assert.All(merged.Sections.Skip(2), s => Assert.True(s.Enabled));
    }

    [Fact]
    public void Merge_appends_from_anime_enabled_to_a_layout_stored_before_it_existed()
    {
        var stored = new HomeLayoutSpec(true, [
            new PageSection(HomeSections.Glance),
            new PageSection(HomeSections.Downloading),
            new PageSection(HomeSections.ContinueReading),
            new PageSection(HomeSections.JumpBackIn, Enabled: false),
            new PageSection(HomeSections.RecentlyAdded),
            new PageSection(HomeSections.Recommended),
            new PageSection(HomeSections.Popular),
        ]);

        var merged = stored.Merge();

        var last = merged.Sections![^1];
        Assert.Equal(HomeSections.FromAnime, last.Key);
        Assert.True(last.Enabled);
        Assert.Null(last.Hero);
        Assert.Equal(HomeSections.All.Length, merged.Sections.Count);
    }

    [Fact]
    public void From_anime_ships_right_after_jump_back_in()
    {
        var keys = HomeLayoutSpec.Default.Sections!.Select(s => s.Key).ToList();

        Assert.Equal(keys.IndexOf(HomeSections.JumpBackIn) + 1, keys.IndexOf(HomeSections.FromAnime));
    }

    [Fact]
    public void Merge_drops_keys_this_build_does_not_know()
    {
        var stored = new HomeLayoutSpec(true, [
            new PageSection("a-section-from-the-future"),
            new PageSection(HomeSections.Popular),
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
            new PageSection(HomeSections.Popular, Enabled: false),
            new PageSection(HomeSections.Popular, Enabled: true),
        ]);

        var merged = stored.Merge();

        Assert.Equal(HomeSections.All.Length, merged.Sections!.Count);
        Assert.False(merged.Sections.Single(s => s.Key == HomeSections.Popular).Enabled);
    }

    [Fact]
    public void Merge_keeps_a_stored_hero_false_and_fills_missing_hero_from_the_default()
    {
        var stored = new HomeLayoutSpec(true, [
            new PageSection(HomeSections.ContinueReading, Hero: false),
            new PageSection(HomeSections.JumpBackIn),
        ]);

        var merged = stored.Merge();

        Assert.False(merged.Sections!.Single(s => s.Key == HomeSections.ContinueReading).Hero);
        // Missing on a hero-capable section is filled from the definition's default (jumpback = false).
        Assert.False(merged.Sections!.Single(s => s.Key == HomeSections.JumpBackIn).Hero);
    }

    [Fact]
    public void Merge_strips_a_hero_flag_stored_on_a_non_hero_section()
    {
        var stored = new HomeLayoutSpec(true, [
            new PageSection(HomeSections.Popular, Hero: true),
        ]);

        var merged = stored.Merge();

        Assert.Null(merged.Sections!.Single(s => s.Key == HomeSections.Popular).Hero);
    }

    [Fact]
    public void Round_trips_order_disabled_flags_and_the_master_switch()
    {
        var spec = new HomeLayoutSpec(false, [
            new PageSection(HomeSections.RecentlyAdded),
            new PageSection(HomeSections.ContinueReading, Enabled: false),
        ]);

        var parsed = HomeLayoutSpec.Parse(HomeLayoutSpec.Serialize(spec));

        Assert.False(parsed.Enabled);
        Assert.Equal(HomeSections.RecentlyAdded, parsed.Sections![0].Key);
        Assert.False(parsed.Sections[1].Enabled);
    }

    [Fact]
    public void Serialized_json_is_camel_case_and_omits_null_hero()
    {
        // The frontend reads this blob directly; a casing change would silently yield defaults.
        var json = HomeLayoutSpec.Serialize(HomeLayoutSpec.Default);

        Assert.Contains("\"enabled\"", json);
        Assert.Contains("\"sections\"", json);
        Assert.Contains("\"key\"", json);
        // Sections with no hero default (e.g. recentlyAdded) must not carry a "hero":null in the blob.
        Assert.DoesNotContain("\"hero\":null", json);
    }

    [Fact]
    public void Merge_keeps_a_known_rail_key_with_its_disabled_state()
    {
        var stored = new HomeLayoutSpec(true, [
            new PageSection(HomeSections.Glance),
            new PageSection(HomeSections.RailKey(5), Enabled: false),
        ]);

        var merged = stored.Merge([5]);

        var rail = merged.Sections!.Single(s => s.Key == HomeSections.RailKey(5));
        Assert.False(rail.Enabled);
    }

    [Fact]
    public void Merge_drops_stale_and_malformed_rail_keys()
    {
        var stored = new HomeLayoutSpec(true, [
            new PageSection(HomeSections.RailKey(5)), // stale: 5 is not one of the caller's rails below
            new PageSection("rail:x"),
            new PageSection("rail:0"),
            new PageSection(HomeSections.Glance),
        ]);

        var merged = stored.Merge([7]);

        Assert.DoesNotContain(merged.Sections!, s => s.Key == HomeSections.RailKey(5));
        Assert.DoesNotContain(merged.Sections!, s => s.Key == "rail:x");
        Assert.DoesNotContain(merged.Sections!, s => s.Key == "rail:0");
        Assert.Contains(merged.Sections!, s => s.Key == HomeSections.RailKey(7));
    }

    [Fact]
    public void Merge_appends_new_rails_enabled_after_the_static_sections_in_the_given_order()
    {
        var merged = HomeLayoutSpec.Default.Merge([9, 3]);

        var tail = merged.Sections!.Skip(HomeSections.All.Length).ToList();
        Assert.Equal([HomeSections.RailKey(9), HomeSections.RailKey(3)], tail.Select(s => s.Key));
        Assert.All(tail, s => Assert.True(s.Enabled));
    }

    [Fact]
    public void Parse_with_no_stored_layout_still_includes_the_callers_rails()
    {
        var spec = HomeLayoutSpec.Parse(null, [4]);

        Assert.Contains(spec.Sections!, s => s.Key == HomeSections.RailKey(4));
    }

    [Fact]
    public void Serialize_with_rail_ids_round_trips_through_parse()
    {
        var spec = new HomeLayoutSpec(true, [new PageSection(HomeSections.RailKey(2))]);

        var json = HomeLayoutSpec.Serialize(spec, [2]);
        var parsed = HomeLayoutSpec.Parse(json, [2]);

        Assert.Equal(HomeSections.RailKey(2), parsed.Sections![0].Key);
    }

    [Fact]
    public void MergePanels_drops_unknown_panels_and_collapses_duplicates()
    {
        var stored = new HomeLayoutSpec(true, [
            new PageSection(HomeSections.Glance, Panels:
            [
                new PagePanel("from-the-future"),
                new PagePanel(HomeGlancePanels.Stats, Enabled: false),
                new PagePanel(HomeGlancePanels.Stats, Enabled: true),
            ]),
        ]);

        var merged = stored.Merge();

        var panels = merged.Sections!.Single(s => s.Key == HomeSections.Glance).Panels!;
        Assert.DoesNotContain(panels, p => p.Key == "from-the-future");
        Assert.Equal(HomeGlancePanels.All, panels.Select(p => p.Key));
        Assert.False(panels.Single(p => p.Key == HomeGlancePanels.Stats).Enabled);
    }

    // --- MigrateGlance -------------------------------------------------------------------------

    [Fact]
    public void Migration_folds_legacy_panels_into_glance_at_the_first_legacy_keys_position()
    {
        var stored = new HomeLayoutSpec(true, [
            new PageSection(HomeSections.ContinueReading),
            new PageSection(HomeGlancePanels.ToRead, Enabled: false),
            new PageSection(HomeGlancePanels.Stats),
            new PageSection(HomeGlancePanels.Progress),
        ]);

        var merged = stored.Merge();

        var keys = merged.Sections!.Select(s => s.Key).ToList();
        Assert.Equal(HomeSections.ContinueReading, keys[0]);
        Assert.Equal(HomeSections.Glance, keys[1]);
        Assert.DoesNotContain(HomeGlancePanels.ToRead, keys);
        Assert.DoesNotContain(HomeGlancePanels.Stats, keys);
        Assert.DoesNotContain(HomeGlancePanels.Progress, keys);

        var glance = merged.Sections![1];
        Assert.True(glance.Enabled); // stats and progress were on
        Assert.Equal(
            [HomeGlancePanels.ToRead, HomeGlancePanels.Stats, HomeGlancePanels.Progress],
            glance.Panels!.Select(p => p.Key));
        Assert.False(glance.Panels!.Single(p => p.Key == HomeGlancePanels.ToRead).Enabled);
        Assert.True(glance.Panels!.Single(p => p.Key == HomeGlancePanels.Stats).Enabled);
    }

    [Fact]
    public void Migration_with_all_legacy_panels_off_turns_the_section_off_and_all_panels_on()
    {
        var stored = new HomeLayoutSpec(true, [
            new PageSection(HomeGlancePanels.Stats, Enabled: false),
            new PageSection(HomeGlancePanels.Progress, Enabled: false),
            new PageSection(HomeGlancePanels.ToRead, Enabled: false),
        ]);

        var merged = stored.Merge();

        var glance = merged.Sections!.Single(s => s.Key == HomeSections.Glance);
        Assert.False(glance.Enabled);
        Assert.All(glance.Panels!, p => Assert.True(p.Enabled));
    }

    [Fact]
    public void Migration_appends_missing_panels_on_when_only_one_legacy_key_was_stored()
    {
        var stored = new HomeLayoutSpec(true, [
            new PageSection(HomeGlancePanels.Stats, Enabled: true),
        ]);

        var merged = stored.Merge();

        var glance = merged.Sections!.Single(s => s.Key == HomeSections.Glance);
        Assert.True(glance.Enabled);
        Assert.Equal(HomeGlancePanels.All, glance.Panels!.Select(p => p.Key));
        Assert.True(glance.Panels!.Single(p => p.Key == HomeGlancePanels.Stats).Enabled);
        Assert.True(glance.Panels!.Single(p => p.Key == HomeGlancePanels.Progress).Enabled);
        Assert.True(glance.Panels!.Single(p => p.Key == HomeGlancePanels.ToRead).Enabled);
    }

    [Fact]
    public void A_stored_glance_section_wins_over_leftover_legacy_keys()
    {
        // Should not happen in practice (a glance section implies migration already ran), but the
        // rule is explicit: a real glance section is never overwritten by leftovers beside it.
        var stored = new HomeLayoutSpec(true, [
            new PageSection(HomeSections.Glance, Panels: [new PagePanel(HomeGlancePanels.Stats, Enabled: false)]),
            new PageSection(HomeGlancePanels.Progress),
        ]);

        var merged = stored.Merge();

        Assert.Single(merged.Sections!, s => s.Key == HomeSections.Glance);
        Assert.DoesNotContain(merged.Sections!, s => s.Key == HomeGlancePanels.Progress);
        var glance = merged.Sections!.Single(s => s.Key == HomeSections.Glance);
        Assert.False(glance.Panels!.Single(p => p.Key == HomeGlancePanels.Stats).Enabled);
    }
}
