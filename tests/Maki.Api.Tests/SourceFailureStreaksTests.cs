using Maki.Api.Services;

namespace Maki.Api.Tests;

/// <summary>
/// When a source that could not be asked stops holding back the "link one by hand" alert: once it has
/// failed several searches in a row, and again only until it answers once.
/// </summary>
public class SourceFailureStreaksTests
{
    [Fact]
    public void A_source_counts_as_down_only_after_failing_several_searches_in_a_row()
    {
        var streaks = new SourceFailureStreaks();
        for (var i = 1; i < SourceFailureStreaks.Threshold; i++)
        {
            streaks.Record(["mangadex"], ["suwayomi:comick"]);
            Assert.False(streaks.IsPersistent("suwayomi:comick"));
        }

        streaks.Record(["mangadex"], ["Suwayomi:Comick"]);

        Assert.True(streaks.IsPersistent("suwayomi:comick"));
        Assert.False(streaks.IsPersistent("mangadex"));
    }

    [Fact]
    public void One_answer_resets_the_streak()
    {
        var streaks = new SourceFailureStreaks();
        for (var i = 0; i < SourceFailureStreaks.Threshold; i++)
        {
            streaks.Record([], ["weebcentral"]);
        }

        streaks.Record(["weebcentral"], []);
        streaks.Record([], ["weebcentral"]);

        Assert.False(streaks.IsPersistent("weebcentral"));
    }
}
