using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using Maki.Api.Services;
using Maki.Core.Sources;

namespace Maki.Api.Tests;

/// <summary>
/// Every download failure used to be stored as <c>error.download.unexpected</c> with no detail, so a
/// MANGA Plus "Invalid user" answer read as "Failed" in Activity with nothing to say why.
/// </summary>
public class DownloadFailureReasonTests
{
    [Fact]
    public void A_site_error_body_keeps_the_sites_own_words() =>
        Assert.Equal(
            (DownloadFailureReason.SourceError, "Invalid user: Invalid user access(11302)"),
            DownloadFailureReason.Classify(new SourceErrorException("Invalid user: Invalid user access(11302)")));

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, DownloadFailureReason.SourceRejected, "HTTP 403")]
    [InlineData(HttpStatusCode.Unauthorized, DownloadFailureReason.SourceRejected, "HTTP 401")]
    [InlineData(HttpStatusCode.BadGateway, DownloadFailureReason.SourceError, "HTTP 502")]
    public void An_http_status_names_the_status(HttpStatusCode status, string key, string detail) =>
        Assert.Equal((key, detail),
            DownloadFailureReason.Classify(new HttpRequestException("boom", null, status)));

    [Fact]
    public void A_connection_failure_is_a_network_failure_and_says_what_broke()
    {
        var ssl = new HttpRequestException(
            "The SSL connection could not be established, see inner exception.",
            new AuthenticationException("The remote certificate is invalid"));

        Assert.Equal((DownloadFailureReason.Network, "The remote certificate is invalid"),
            DownloadFailureReason.Classify(ssl));
        Assert.Equal(DownloadFailureReason.Network,
            DownloadFailureReason.Classify(new HttpRequestException("refused", new SocketException())).Key);
        Assert.Equal(DownloadFailureReason.Network,
            DownloadFailureReason.Classify(new TaskCanceledException("HttpClient.Timeout of 60 seconds elapsing")).Key);
    }

    [Fact]
    public void A_write_failure_is_a_disk_failure() =>
        Assert.Equal(DownloadFailureReason.Disk,
            DownloadFailureReason.Classify(new IOException("No space left on device")).Key);

    [Fact]
    public void A_chapter_no_source_lists_says_what_each_source_answered()
    {
        var (key, detail) = DownloadFailureReason.Classify(new ChapterUnavailableException(
            ChapterUnavailableException.NotListed, "Chapter 3 unavailable on all sources (mangaplus: chapter not listed)"));

        Assert.Equal("error.download.notListed", key);
        Assert.Contains("mangaplus: chapter not listed", detail);
    }

    [Fact]
    public void Anything_else_is_unexpected_and_carries_its_message()
    {
        var (key, detail) = DownloadFailureReason.Classify(new InvalidOperationException(new string('x', 400)));

        Assert.Equal(DownloadFailureReason.Unexpected, key);
        Assert.Equal(303, detail!.Length);
    }
}
