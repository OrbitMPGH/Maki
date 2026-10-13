using Maki.Api.Services;

namespace Maki.Api.Tests;

public class SourceFailoverTests
{
    [Theory]
    [InlineData(DownloadFailureReason.SourceRejected)]
    [InlineData(DownloadFailureReason.SourceError)]
    [InlineData(DownloadFailureReason.Network)]
    public void The_third_source_side_failure_moves_to_the_next_source(string key)
    {
        Assert.False(SourceFailover.ShouldFailOver(key, 1, false, false, false));
        Assert.False(SourceFailover.ShouldFailOver(key, 2, false, false, false));
        Assert.True(SourceFailover.ShouldFailOver(key, 3, false, false, false));
        Assert.False(SourceFailover.ShouldFailOver(key, 4, false, false, false));
        Assert.True(SourceFailover.ShouldFailOver(key, 6, false, false, false));
    }

    [Theory]
    [InlineData(DownloadFailureReason.ChallengeNotSolved)]
    [InlineData(DownloadFailureReason.Disk)]
    [InlineData(DownloadFailureReason.Unexpected)]
    [InlineData("error.download.noPages")]
    [InlineData("error.download.rateLimited")]
    public void Other_failures_stay_on_their_source(string key) =>
        Assert.False(SourceFailover.ShouldFailOver(key, 3, false, false, false));

    [Fact]
    public void A_pinned_repair_or_upgrade_item_never_leaves_its_source()
    {
        Assert.False(SourceFailover.ShouldFailOver(DownloadFailureReason.Network, 3, true, false, false));
        Assert.False(SourceFailover.ShouldFailOver(DownloadFailureReason.Network, 3, false, true, false));
        Assert.False(SourceFailover.ShouldFailOver(DownloadFailureReason.Network, 3, false, false, true));
    }

    [Fact]
    public void A_classified_exception_decides_the_same_way()
    {
        var (key, _) = DownloadFailureReason.Classify(new HttpRequestException("boom", null, System.Net.HttpStatusCode.BadGateway));
        Assert.True(SourceFailover.ShouldFailOver(key, 3, false, false, false));
    }
}
