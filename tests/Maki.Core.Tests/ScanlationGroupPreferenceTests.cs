using Maki.Core.Sources;

namespace Maki.Core.Tests;

public class ScanlationGroupPreferenceTests
{
    private static ScanlationGroupPreference Of(string[]? preferred = null, string[]? blocked = null) =>
        ScanlationGroupPreference.Of(preferred, blocked);

    [Fact]
    public void A_series_preferred_list_replaces_the_global_one()
    {
        var combined = ScanlationGroupPreference.Combine(Of(preferred: ["A", "B"]), Of(preferred: ["C"]));

        Assert.Equal(["C"], combined.Preferred);
    }

    [Fact]
    public void An_empty_series_preferred_list_keeps_the_global_one()
    {
        var combined = ScanlationGroupPreference.Combine(Of(preferred: ["A", "B"]), Of());

        Assert.Equal(["A", "B"], combined.Preferred);
    }

    [Fact]
    public void Blocked_lists_add_up()
    {
        var combined = ScanlationGroupPreference.Combine(Of(blocked: ["X"]), Of(blocked: ["Y", "x"]));

        Assert.Equal(["X", "Y"], combined.Blocked);
    }

    [Fact]
    public void The_series_wins_a_conflict_with_the_global_lists()
    {
        var combined = ScanlationGroupPreference.Combine(
            Of(preferred: ["A", "B"], blocked: ["C"]),
            Of(blocked: ["a"]));
        Assert.Equal(["B"], combined.Preferred);

        combined = ScanlationGroupPreference.Combine(Of(blocked: ["C"]), Of(preferred: ["c"]));
        Assert.Empty(combined.Blocked);
        Assert.Equal(["c"], combined.Preferred);
    }

    [Fact]
    public void Names_are_trimmed_deduplicated_and_blank_ones_dropped()
    {
        var groups = Of(preferred: [" A ", "a", "", "  ", "B"]);

        Assert.Equal(["A", "B"], groups.Preferred);
    }

    [Fact]
    public void A_joint_credit_matches_each_member()
    {
        var groups = Of(preferred: ["B"], blocked: ["C"]);

        Assert.Equal(0, groups.Rank("A, B"));
        Assert.True(groups.IsBlocked("C, D"));
        Assert.False(groups.IsBlocked("A, B"));
        Assert.False(groups.IsBlocked(null));
        Assert.Null(groups.Rank(null));
    }

    [Fact]
    public void The_cache_key_ignores_blocked_order_but_not_preferred_order()
    {
        Assert.Equal(Of(blocked: ["X", "Y"]).Key, Of(blocked: ["y", "x"]).Key);
        Assert.NotEqual(Of(preferred: ["A", "B"]).Key, Of(preferred: ["B", "A"]).Key);
        Assert.Equal(string.Empty, ScanlationGroupPreference.None.Key);
    }
}
