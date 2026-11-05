using Maki.Core.Quality;

namespace Maki.Core.Tests;

public class SourceReliabilityTests
{
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(0, 2, false)]
    [InlineData(0, 3, true)]
    [InlineData(10, 3, false)]
    [InlineData(40, 125, true)]
    [InlineData(40, 100, false)]
    [InlineData(1, 3, true)]
    [InlineData(1, 2, false)]
    public void A_source_is_unreliable_once_it_fails_three_times_for_every_success(int completed, int failed, bool expected) =>
        Assert.Equal(expected, SourceReliability.IsUnreliable(new SourceReliability.Outcome(completed, failed)));

    [Fact]
    public void Unreliable_sources_move_to_the_end_and_keep_their_order()
    {
        var outcomes = new Dictionary<string, SourceReliability.Outcome>
        {
            ["a"] = new(0, 5), ["b"] = new(3, 0), ["c"] = new(0, 4), ["d"] = new(0, 0)
        };

        var ordered = SourceReliability.Demote(["a", "b", "c", "d"], name => SourceReliability.IsUnreliable(outcomes[name]));

        Assert.Equal(["b", "d", "a", "c"], ordered);
    }

    [Fact]
    public void Nothing_moves_when_every_source_is_reliable() =>
        Assert.Equal(["a", "b"], SourceReliability.Demote(["a", "b"], _ => false));
}
