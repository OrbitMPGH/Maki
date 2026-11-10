using Maki.Core.Quality;

namespace Maki.Core.Tests.Quality;

public class ProposalAutoApprovalTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Theory]
    [InlineData(SpanVerdictReasons.WholeSeriesPack)]
    [InlineData(SpanVerdictReasons.AddsMissingChapters)]
    [InlineData(SpanVerdictReasons.OverBudget)]
    public void A_digital_release_within_the_ceiling_is_allowed_for_each_reason_that_costs_nothing_to_check(string reason) =>
        Assert.True(ProposalAutoApproval.Allows([reason], 3 * Gb, true, 12 * Gb));

    [Fact]
    public void All_three_reasons_together_are_still_allowed() =>
        Assert.True(ProposalAutoApproval.Allows(
            [SpanVerdictReasons.WholeSeriesPack, SpanVerdictReasons.AddsMissingChapters, SpanVerdictReasons.OverBudget],
            3 * Gb, true, 12 * Gb));

    [Theory]
    [InlineData(SpanVerdictReasons.UnknownChapters)]
    [InlineData(SpanVerdictReasons.VolumeSpanUnknown)]
    [InlineData(SpanVerdictReasons.TrailingNumber)]
    [InlineData(SpanVerdictReasons.MarkersOff)]
    [InlineData(SpanVerdictReasons.UpgradesDisabled)]
    [InlineData(SpanVerdictReasons.TitleUncertain)]
    [InlineData(SpanVerdictReasons.TitleRelatedSeries)]
    public void A_reason_that_says_maki_is_unsure_keeps_the_proposal_waiting(string reason)
    {
        Assert.False(ProposalAutoApproval.Allows([reason], Gb, true, 12 * Gb));
        Assert.False(ProposalAutoApproval.Allows([SpanVerdictReasons.WholeSeriesPack, reason], Gb, true, 12 * Gb));
    }

    [Fact]
    public void A_release_over_the_ceiling_waits()
    {
        Assert.False(ProposalAutoApproval.Allows([SpanVerdictReasons.WholeSeriesPack], 13 * Gb, true, 12 * Gb));
        Assert.True(ProposalAutoApproval.Allows([SpanVerdictReasons.WholeSeriesPack], 12 * Gb, true, 12 * Gb));
    }

    [Fact]
    public void A_release_that_is_not_digital_waits() =>
        Assert.False(ProposalAutoApproval.Allows([SpanVerdictReasons.WholeSeriesPack], Gb, false, 12 * Gb));

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void A_release_of_unknown_size_waits(long size) =>
        Assert.False(ProposalAutoApproval.Allows([SpanVerdictReasons.WholeSeriesPack], size, true, 12 * Gb));

    [Fact]
    public void A_proposal_with_no_reasons_is_not_something_this_rule_decides() =>
        Assert.False(ProposalAutoApproval.Allows([], Gb, true, 12 * Gb));

    [Theory]
    [InlineData(0L, ProposalAutoApproval.MinMaxBytes)]
    [InlineData(5L * Gb, 5L * Gb)]
    [InlineData(10000L * Gb, ProposalAutoApproval.MaxMaxBytes)]
    public void The_ceiling_is_kept_inside_sane_bounds(long value, long expected) =>
        Assert.Equal(expected, ProposalAutoApproval.ClampMaxBytes(value));

    [Fact]
    public void The_default_ceiling_is_about_fifty_volumes() =>
        Assert.InRange(ProposalAutoApproval.DefaultMaxBytes / (50 * 1024L * 1024), 240, 260);
}
