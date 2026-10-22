using Maki.Core.Configuration;

namespace Maki.Core.Tests;

/// <summary>
/// The Home layout blob. Most of these pin <see cref="HomeLayoutSpec.Merge()"/> (via
/// <see cref="PageLayouts.Merge"/>), which is what keeps a stored layout working across a release
/// that adds or removes a section, and the glance migrations: <see cref="HomeLayoutSpec.MigrateGlance"/>
/// folds the pre-glance stats/progress/toread sections into one, and
/// <see cref="HomeLayoutSpec.MigrateGlanceIntoContinue"/> folds that section into Continue reading's
/// options.
/// </summary>
public class HomeLayoutTests
{
    [Fact]
    public void Default_has_every_section_in_shipping_order_with_discovery_off()
    {
        var spec = HomeLayoutSpec.Default;

        Assert.True(spec.Enabled);
        Assert.Equal(HomeSections.All, spec.Sections!.Select(s => s.Key));
        Assert.All(spec.Sections!, s => Assert.Equal(!HomeSections.DefaultOff.Contains(s.Key), s.Enabled));
        Assert.Equal([HomeSections.Recommended, HomeSections.Popular], HomeSections.DefaultOff);
    }

    [Fact]
    public void Default_opens_on_continue_reading_with_jump_back_in_next()
    {
        var keys = HomeLayoutSpec.Default.Sections!.Select(s => s.Key).ToList();

        Assert.Equal(HomeSections.ContinueReading, keys[0]);
        Assert.Equal(HomeSections.JumpBackIn, keys[1]);
        Assert.DoesNotContain(HomeSections.Glance, keys);
        Assert.True(keys.IndexOf(HomeSections.Downloading) > keys.IndexOf(HomeSections.RecentlyAdded));
    }

    [Fact]
    public void Default_continue_shows_the_glance_with_next_up_in_a_tray()
    {
        var sections = HomeLayoutSpec.Default.Sections!;

        var cont = sections.Single(s => s.Key == HomeSections.ContinueReading);
        Assert.Equal(new Dictionary<string, bool> { ["glance"] = true, ["nextUpBeside"] = false }, cont.Options);
        var jumpBack = sections.Single(s => s.Key == HomeSections.JumpBackIn);
        Assert.Equal(new Dictionary<string, bool> { ["nextUpBeside"] = false }, jumpBack.Options);
        Assert.All(
            sections.Where(s => s.Key is not HomeSections.ContinueReading and not HomeSections.JumpBackIn),
            s => Assert.Null(s.Options));
    }

    [Fact]
    public void Merge_keeps_stored_options_fills_missing_ones_and_drops_unknown_ones()
    {
        var stored = new HomeLayoutSpec(true, [
            new PageSection(HomeSections.ContinueReading, Options: new Dictionary<string, bool>
            {
                [HomeHeroOptions.NextUpBeside] = true,
                ["from-the-future"] = true,
            }),
            new PageSection(HomeSections.Popular, Options: new Dictionary<string, bool> { ["glance"] = true }),
        ]);

        var merged = stored.Merge();

        var cont = merged.Sections!.Single(s => s.Key == HomeSections.ContinueReading);
        Assert.Equal(new Dictionary<string, bool> { ["glance"] = true, ["nextUpBeside"] = true }, cont.Options);
        Assert.Null(merged.Sections!.Single(s => s.Key == HomeSections.Popular).Options);
    }

    [Fact]
    public void Options_round_trip_through_the_blob_in_camel_case()
    {
        var spec = new HomeLayoutSpec(true, [
            new PageSection(HomeSections.ContinueReading, Options: new Dictionary<string, bool>
            {
                [HomeHeroOptions.Glance] = false,
                [HomeHeroOptions.NextUpBeside] = true,
            }),
        ]);

        var json = HomeLayoutSpec.Serialize(spec);
        var parsed = HomeLayoutSpec.Parse(json);

        Assert.Contains("\"options\":{\"glance\":false,\"nextUpBeside\":true}", json);
        Assert.DoesNotContain("\"options\":null", json);
        var cont = parsed.Sections!.Single(s => s.Key == HomeSections.ContinueReading);
        Assert.False(cont.Options![HomeHeroOptions.Glance]);
        Assert.True(cont.Options[HomeHeroOptions.NextUpBeside]);
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
            new PageSection(HomeSections.JumpBackIn),
            new PageSection(HomeSections.ContinueReading, Enabled: false),
        ]);

        var merged = stored.Merge();

        // The user's own order survives at the front...
        Assert.Equal(HomeSections.JumpBackIn, merged.Sections![0].Key);
        Assert.Equal(HomeSections.ContinueReading, merged.Sections[1].Key);
        Assert.False(merged.Sections[1].Enabled);
        // ...and everything new lands after it, on unless it ships off, rather than jumping to its canonical slot.
        Assert.Equal(HomeSections.All.Length, merged.Sections.Count);
        Assert.All(
            merged.Sections.Skip(2),
            s => Assert.Equal(!HomeSections.DefaultOff.Contains(s.Key), s.Enabled));
    }

    [Fact]
    public void Merge_appends_from_anime_enabled_to_a_layout_stored_before_it_existed()
    {
        var stored = new HomeLayoutSpec(true, [
            new PageSection(HomeSections.Downloading),
            new PageSection(HomeSections.ContinueReading),
            new PageSection(HomeSections.JumpBackIn, Enabled: false),
            new PageSection(HomeSections.RecentlyAdded),
            new PageSection(HomeSections.Recommended),
            new PageSection(HomeSections.Popular),
        ]);

        var merged = stored.Merge();

        var appended = merged.Sections!.Skip(stored.Sections!.Count).ToList();
        var fromAnime = Assert.Single(appended, s => s.Key == HomeSections.FromAnime);
        Assert.True(fromAnime.Enabled);
        Assert.Null(fromAnime.Hero);
        Assert.Contains(appended, s => s.Key == HomeSections.Following && s.Enabled);
        Assert.Equal(HomeSections.All.Length, merged.Sections!.Count);
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
            new PageSection(HomeSections.RecentlyAdded),
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
            new PageSection(HomeSections.RecentlyAdded),
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
        var merged = PageLayouts.MergePanels(
            [new PagePanel("from-the-future"), new PagePanel("a", Enabled: false), new PagePanel("a", Enabled: true)],
            ["a", "b"]);

        Assert.Equal(["a", "b"], merged.Select(p => p.Key));
        Assert.False(merged[0].Enabled);
        Assert.True(merged[1].Enabled);
    }

    private static bool GlanceOf(HomeLayoutSpec merged) =>
        merged.Sections!.Single(s => s.Key == HomeSections.ContinueReading).Options![HomeHeroOptions.Glance];

    // --- MigrateGlanceIntoContinue ------------------------------------------------------------

    [Fact]
    public void A_stored_glance_section_becomes_continues_glance_switch_and_is_dropped()
    {
        var stored = new HomeLayoutSpec(true, [
            new PageSection(HomeSections.Downloading),
            new PageSection(HomeSections.Glance, Enabled: false),
            new PageSection(HomeSections.ContinueReading),
        ]);

        var merged = stored.Merge();

        Assert.DoesNotContain(merged.Sections!, s => s.Key == HomeSections.Glance);
        Assert.Equal(HomeSections.Downloading, merged.Sections![0].Key);
        Assert.Equal(HomeSections.ContinueReading, merged.Sections[1].Key);
        Assert.False(GlanceOf(merged));
    }

    [Fact]
    public void A_stored_glance_with_panels_comes_through_as_the_switch_on()
    {
        var stored = new HomeLayoutSpec(true, [
            new PageSection(HomeSections.ContinueReading),
            new PageSection(HomeSections.Glance, Panels:
            [
                new PagePanel(HomeGlancePanels.Stats, Enabled: false),
                new PagePanel(HomeGlancePanels.Progress),
            ]),
        ]);

        var merged = stored.Merge();

        Assert.True(GlanceOf(merged));
        Assert.All(merged.Sections!, s => Assert.Null(s.Panels));
    }

    [Fact]
    public void A_glance_stored_with_every_panel_off_comes_through_as_the_switch_off()
    {
        var stored = new HomeLayoutSpec(true, [
            new PageSection(HomeSections.ContinueReading),
            new PageSection(HomeSections.Glance, Panels:
            [
                new PagePanel(HomeGlancePanels.Stats, Enabled: false),
                new PagePanel(HomeGlancePanels.Progress, Enabled: false),
                new PagePanel(HomeGlancePanels.ToRead, Enabled: false),
            ]),
        ]);

        Assert.False(GlanceOf(stored.Merge()));
    }

    [Fact]
    public void A_glance_stored_without_a_continue_section_falls_back_to_the_default()
    {
        var stored = new HomeLayoutSpec(true, [new PageSection(HomeSections.Glance, Enabled: false)]);

        var merged = stored.Merge();

        Assert.DoesNotContain(merged.Sections!, s => s.Key == HomeSections.Glance);
        Assert.True(GlanceOf(merged));
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
        Assert.DoesNotContain(HomeSections.Glance, keys);
        Assert.DoesNotContain(HomeGlancePanels.ToRead, keys);
        Assert.DoesNotContain(HomeGlancePanels.Stats, keys);
        Assert.DoesNotContain(HomeGlancePanels.Progress, keys);

        Assert.True(GlanceOf(merged)); // stats and progress were on
        Assert.All(merged.Sections!, s => Assert.Null(s.Panels));
    }

    [Fact]
    public void Migration_with_all_legacy_panels_off_turns_the_section_off()
    {
        var stored = new HomeLayoutSpec(true, [
            new PageSection(HomeSections.ContinueReading),
            new PageSection(HomeGlancePanels.Stats, Enabled: false),
            new PageSection(HomeGlancePanels.Progress, Enabled: false),
            new PageSection(HomeGlancePanels.ToRead, Enabled: false),
        ]);

        Assert.False(GlanceOf(stored.Merge()));
    }

    [Fact]
    public void Migration_turns_the_section_on_when_only_one_legacy_key_was_stored()
    {
        var stored = new HomeLayoutSpec(true, [
            new PageSection(HomeSections.ContinueReading, Options: new Dictionary<string, bool> { [HomeHeroOptions.Glance] = false }),
            new PageSection(HomeGlancePanels.Stats, Enabled: true),
        ]);

        // The migrated flag wins over an option the same blob could not have stored.
        Assert.True(GlanceOf(stored.Merge()));
    }

    [Fact]
    public void A_stored_glance_section_wins_over_leftover_legacy_keys()
    {
        // Should not happen in practice (a glance section implies migration already ran), but the
        // rule is explicit: a real glance section is never overwritten by leftovers beside it.
        var stored = new HomeLayoutSpec(true, [
            new PageSection(HomeSections.ContinueReading),
            new PageSection(HomeSections.Glance, Enabled: false),
            new PageSection(HomeGlancePanels.Progress),
        ]);

        var merged = stored.Merge();

        Assert.DoesNotContain(merged.Sections!, s => s.Key == HomeSections.Glance);
        Assert.DoesNotContain(merged.Sections!, s => s.Key == HomeGlancePanels.Progress);
        Assert.False(GlanceOf(merged));
    }

    private static List<PageSection> PreviousDefault() =>
        HomeSections.HeroFirstPredecessor.Select(key => new PageSection(key)).ToList();

    [Fact]
    public void A_layout_still_on_the_previous_default_moves_to_the_new_one()
    {
        var merged = new HomeLayoutSpec(true, PreviousDefault()).Merge();

        Assert.Equal(HomeSections.All, merged.Sections!.Select(s => s.Key));
        Assert.All(merged.Sections!, s => Assert.True(s.Enabled));
    }

    [Fact]
    public void The_previous_default_with_its_glance_section_in_front_still_moves()
    {
        var stored = PreviousDefault();
        stored.Insert(0, new PageSection(HomeSections.Glance));

        var merged = new HomeLayoutSpec(true, stored).Merge();

        Assert.Equal(HomeSections.All, merged.Sections!.Select(s => s.Key));
        Assert.True(GlanceOf(merged));
    }

    [Fact]
    public void The_shipping_order_before_the_glance_became_a_switch_is_left_as_it_is()
    {
        // Continue, glance, jump back in, ...: the default the previous build wrote. Folding the
        // glance away leaves it equal to the current order, so nothing moves.
        var stored = HomeSections.All.Select(key => new PageSection(key)).ToList();
        stored.Insert(1, new PageSection(HomeSections.Glance, Enabled: false));

        var merged = new HomeLayoutSpec(true, stored).Merge();

        Assert.Equal(HomeSections.All, merged.Sections!.Select(s => s.Key));
        Assert.False(GlanceOf(merged));
    }

    [Fact]
    public void The_move_keeps_custom_rails_after_the_built_in_sections()
    {
        var stored = PreviousDefault();
        stored.Add(new PageSection(HomeSections.RailKey(4), Enabled: false));

        var merged = new HomeLayoutSpec(true, stored).Merge([4]);

        Assert.Equal(HomeSections.RailKey(4), merged.Sections![^1].Key);
        Assert.False(merged.Sections[^1].Enabled);
        Assert.Equal(HomeSections.All, merged.Sections.Take(HomeSections.All.Length).Select(s => s.Key));
    }

    [Fact]
    public void The_move_keeps_the_switches_the_owner_flipped()
    {
        var stored = PreviousDefault();
        stored[2] = stored[2] with { Enabled = false };
        stored[1] = stored[1] with { Hero = false };
        stored[6] = stored[6] with { Enabled = false };

        var merged = new HomeLayoutSpec(true, stored).Merge();

        Assert.Equal(HomeSections.All, merged.Sections!.Select(s => s.Key));
        Assert.False(merged.Sections!.Single(s => s.Key == HomeSections.JumpBackIn).Enabled);
        Assert.False(merged.Sections!.Single(s => s.Key == HomeSections.ContinueReading).Hero);
        Assert.False(merged.Sections!.Single(s => s.Key == HomeSections.Recommended).Enabled);
    }

    [Fact]
    public void A_layout_in_an_order_the_owner_chose_is_left_where_it_is()
    {
        var reordered = PreviousDefault();
        (reordered[0], reordered[1]) = (reordered[1], reordered[0]);

        var merged = new HomeLayoutSpec(true, reordered).Merge();

        Assert.Equal(reordered.Select(s => s.Key), merged.Sections!.Select(s => s.Key));
    }
}
