using Maki.Api.Services;

namespace Maki.Api.Tests;

public class SourceFailoverTests
{
    [Theory]
    [InlineData(DownloadFailureReason.SourceRejected)]
    [InlineData(DownloadFailureReason.SourceError)]
    [InlineData(DownloadFailureReason.Network)]
    public void Every_nth_source_side_failure_moves_to_the_next_source(string key)
    {
        for (var failures = 1; failures <= SourceFailover.FailuresPerSource * 3; failures++)
        {
            Assert.Equal(
                failures % SourceFailover.FailuresPerSource == 0,
                SourceFailover.ShouldFailOver(key, failures, false, false, false));
        }
    }

    [Theory]
    [InlineData(DownloadFailureReason.ChallengeNotSolved)]
    [InlineData(DownloadFailureReason.Disk)]
    [InlineData(DownloadFailureReason.Unexpected)]
    [InlineData("error.download.noPages")]
    [InlineData("error.download.rateLimited")]
    public void Other_failures_stay_on_their_source(string key) =>
        Assert.False(SourceFailover.ShouldFailOver(key, SourceFailover.FailuresPerSource, false, false, false));

    [Fact]
    public void A_pinned_repair_or_upgrade_item_never_leaves_its_source()
    {
        var due = SourceFailover.FailuresPerSource;
        Assert.True(SourceFailover.ShouldFailOver(DownloadFailureReason.Network, due, false, false, false));
        Assert.False(SourceFailover.ShouldFailOver(DownloadFailureReason.Network, due, true, false, false));
        Assert.False(SourceFailover.ShouldFailOver(DownloadFailureReason.Network, due, false, true, false));
        Assert.False(SourceFailover.ShouldFailOver(DownloadFailureReason.Network, due, false, false, true));
    }

    [Fact]
    public void A_classified_exception_decides_the_same_way()
    {
        var (key, _) = DownloadFailureReason.Classify(new HttpRequestException("boom", null, System.Net.HttpStatusCode.BadGateway));
        Assert.True(SourceFailover.ShouldFailOver(key, SourceFailover.FailuresPerSource, false, false, false));
    }
}
