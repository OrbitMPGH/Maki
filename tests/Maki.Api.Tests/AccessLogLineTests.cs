using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Maki.Api.Logging;
using Maki.Api.Services;
using Microsoft.AspNetCore.Http;

namespace Maki.Api.Tests;

/// <summary>
/// <see cref="AccessLogLine"/> is read by CrowdSec and fail2ban, not by people, so the shape is the
/// contract. The pattern below is the access-log grok from CrowdSec's nginx parser, written as a
/// regex, which the shipped <c>distribution/crowdsec</c> parser also follows.
/// </summary>
public class AccessLogLineTests
{
    private static readonly Regex Combined = new(
        """^(?<ip>\S+) - (?<user>[a-zA-Z0-9.@\-+_%]+) \[(?<time>\d{2}/\w{3}/\d{4}:\d{2}:\d{2}:\d{2} [+-]\d{4})\] "(?<verb>\w+) (?<request>.*?) HTTP/(?<version>[\d.]+)" (?<status>\d+) (?<bytes>\d+) "(?<referer>[^"]*)" "(?<agent>[^"]*)"$""");

    private static readonly DateTimeOffset At = new(2026, 10, 30, 14, 2, 11, TimeSpan.FromHours(1));

    private static DefaultHttpContext Context(string method = "GET", string path = "/api/v1/series", string? query = null, int status = 200)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Protocol = "HTTP/1.1";
        if (query is not null)
            context.Request.QueryString = new QueryString(query);
        context.Response.StatusCode = status;
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        return context;
    }

    private static Match Parse(HttpContext context, bool failed = false)
    {
        var match = Combined.Match(AccessLogLine.Format(context, At, failed));
        Assert.True(match.Success, AccessLogLine.Format(context, At, failed));
        return match;
    }

    [Fact]
    public void An_anonymous_failed_sign_in_reads_as_a_combined_log_line()
    {
        var context = Context("POST", "/api/v1/auth/login", status: 401);
        context.Request.Headers.UserAgent = "Mozilla/5.0 (X11; Linux x86_64)";

        Assert.Equal(
            "203.0.113.7 - - [30/Oct/2026:14:02:11 +0100] \"POST /api/v1/auth/login HTTP/1.1\" 401 0 \"-\" \"Mozilla/5.0 (X11; Linux x86_64)\"",
            AccessLogLine.Format(context, At));
    }

    [Fact]
    public void A_signed_in_request_names_the_account_encoded_for_the_parser()
    {
        var context = Context();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "Ann \"the\" reader")], "Cookies"));

        Assert.Equal("Ann%20%22the%22%20reader", Parse(context).Groups["user"].Value);
    }

    [Fact]
    public void A_token_request_names_the_token_owner()
    {
        var context = Context(path: "/komga/api/v1/series");
        context.Items[typeof(OpdsAccess)] = new OpdsAccess(TrackProgress: true, UserId: 4, AllRootFolders: true, Komga: true);

        Assert.Equal("opds-user-4", Parse(context).Groups["user"].Value);
    }

    [Fact]
    public void An_ipv4_client_on_the_dual_stack_listener_is_written_as_ipv4()
    {
        var context = Context();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7").MapToIPv6();

        Assert.Equal("203.0.113.7", Parse(context).Groups["ip"].Value);
    }

    [Theory]
    [InlineData("/api/v1/opds/secret-token/series/4", "/api/v1/opds/redacted/series/4")]
    [InlineData("/api/v1/opds/secret-token", "/api/v1/opds/redacted")]
    [InlineData("/api/v1/opds", "/api/v1/opds")]
    public void The_opds_token_never_reaches_the_file(string path, string written)
    {
        Assert.Equal(written, Parse(Context(path: path, status: 404)).Groups["request"].Value);
    }

    [Fact]
    public void The_sso_callback_code_and_state_are_masked()
    {
        var context = Context(path: "/api/v1/auth/oidc/complete", query: "?code=abc123&state=xyz&returnUrl=%2Flibrary");

        Assert.Equal(
            "/api/v1/auth/oidc/complete?code=redacted&state=redacted&returnUrl=%2Flibrary",
            Parse(context).Groups["request"].Value);
    }

    [Fact]
    public void Quotes_in_a_header_cannot_end_the_field()
    {
        var context = Context();
        context.Request.Headers.UserAgent = "evil\" 200 0 \"x";

        Assert.Equal("evil\\x22 200 0 \\x22x", Parse(context).Groups["agent"].Value);
    }

    [Fact]
    public void A_throw_before_the_response_started_is_a_500()
    {
        Assert.Equal("500", Parse(Context(), failed: true).Groups["status"].Value);
    }

    [Fact]
    public void A_throw_after_the_client_went_away_is_a_499_not_a_500()
    {
        var context = Context();
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();
        context.RequestAborted = aborted.Token;

        Assert.Equal("499", Parse(context, failed: true).Groups["status"].Value);
    }

    [Fact]
    public void The_target_read_before_the_pipeline_wins_over_a_rewritten_path()
    {
        var context = Context(path: "/wp-login.php");
        var target = AccessLogLine.Target(context.Request);
        context.Request.Path = "/index.html";

        Assert.Equal("/wp-login.php", Combined.Match(AccessLogLine.Format(context, At, target: target)).Groups["request"].Value);
    }

    [Fact]
    public void A_known_length_is_the_byte_count()
    {
        var context = Context();
        context.Response.ContentLength = 5120;

        Assert.Equal("5120", Parse(context).Groups["bytes"].Value);
    }
}
