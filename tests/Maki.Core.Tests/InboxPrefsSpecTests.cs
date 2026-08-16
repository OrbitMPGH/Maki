using Maki.Core.Inbox;

namespace Maki.Core.Tests;

/// <summary>
/// The preference spec has one job beyond storing switches: surviving a release that adds an event
/// type. These pin the merge rules that make that true.
/// </summary>
public class InboxPrefsSpecTests
{
    [Fact]
    public void An_unset_spec_wants_everything_except_the_types_that_default_off()
    {
        var prefs = InboxPrefsSpec.Parse(null);

        Assert.True(prefs.Wants(InboxEventType.NewChapterAvailable));
        Assert.True(prefs.Wants(InboxEventType.AchievementUnlocked));
        Assert.True(prefs.Toasts);

        // Already reflected live in the Sources card; an inbox row for it is a second copy.
        Assert.False(prefs.Wants(InboxEventType.SourceMatchFinished));
    }

    [Fact]
    public void Merge_lists_every_known_type_so_the_settings_card_has_something_to_render()
    {
        var merged = new InboxPrefsSpec().Merge();

        Assert.Equal(InboxEventTypes.All.Length, merged.Types!.Count);
        Assert.All(InboxEventTypes.All, t => Assert.Contains(InboxEventTypes.Key(t), merged.Types.Keys));
        Assert.DoesNotContain(InboxEventTypes.Key(InboxEventType.Unknown), merged.Types.Keys);
    }

    [Fact]
    public void A_type_this_build_added_arrives_enabled_rather_than_off()
    {
        // A spec stored by an older build: it has an opinion about one type and has never heard of
        // the rest. The ones it never saw must not read as "the user turned these off".
        var stored = """{"types":{"levelUp":false},"toasts":true}""";

        var prefs = InboxPrefsSpec.Parse(stored);

        Assert.False(prefs.Wants(InboxEventType.LevelUp));
        Assert.True(prefs.Wants(InboxEventType.ChapterDownloaded));
        Assert.True(prefs.Wants(InboxEventType.RequestApproved));
    }

    [Fact]
    public void A_type_this_build_no_longer_knows_is_dropped()
    {
        var prefs = InboxPrefsSpec.Parse("""{"types":{"somethingRetired":true,"levelUp":true}}""");

        Assert.DoesNotContain("somethingRetired", prefs.Types!.Keys);
        Assert.True(prefs.Wants(InboxEventType.LevelUp));
    }

    [Fact]
    public void An_explicit_false_beats_a_default_of_on_and_survives_a_round_trip()
    {
        var saved = InboxPrefsSpec.Serialize(
            new InboxPrefsSpec(new Dictionary<string, bool> { ["chapterDownloaded"] = false }, Toasts: false));

        var reloaded = InboxPrefsSpec.Parse(saved);

        Assert.False(reloaded.Wants(InboxEventType.ChapterDownloaded));
        Assert.False(reloaded.Toasts);
        Assert.True(reloaded.Wants(InboxEventType.DownloadFailed));
    }

    [Fact]
    public void An_explicit_true_beats_a_default_of_off()
    {
        var prefs = InboxPrefsSpec.Parse("""{"types":{"sourceMatchFinished":true}}""");

        Assert.True(prefs.Wants(InboxEventType.SourceMatchFinished));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"types\":\"wrong shape\"}")]
    public void Unreadable_storage_degrades_to_defaults_rather_than_throwing(string stored)
    {
        var prefs = InboxPrefsSpec.Parse(stored);

        Assert.True(prefs.Wants(InboxEventType.NewChapterAvailable));
        Assert.True(prefs.Toasts);
    }

    [Fact]
    public void Keys_are_camel_case_so_the_client_and_the_store_agree()
    {
        Assert.Equal("newChapterAvailable", InboxEventTypes.Key(InboxEventType.NewChapterAvailable));
        Assert.Equal("levelUp", InboxEventTypes.Key(InboxEventType.LevelUp));
    }

    [Fact]
    public void Unknown_is_never_offered_as_a_preference()
    {
        Assert.DoesNotContain(InboxEventType.Unknown, InboxEventTypes.All);
    }
}
