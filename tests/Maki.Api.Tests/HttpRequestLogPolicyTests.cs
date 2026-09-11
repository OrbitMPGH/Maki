using Maki.Api.Logging;
using Microsoft.AspNetCore.Http;
using Serilog.Events;

namespace Maki.Api.Tests;

/// <summary>
/// <see cref="HttpRequestLogPolicy"/> decides what a finished request costs in the log. The tiers
/// are the whole feature, so they are pinned here rather than left to be discovered by reading a
/// day of output.
/// </summary>
public class HttpRequestLogPolicyTests
{
    private static readonly LoggingOptions Options = LoggingOptions.Defaults;

    private static LogEventLevel Level(
        string method,
        string path,
        int status = 200,
        double elapsedMs = 5,
        Exception? exception = null,
        LoggingOptions? options = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.StatusCode = status;

        return HttpRequestLogPolicy.For(options ?? Options)(context, elapsedMs, exception);
    }

    [Theory]
    [InlineData("/api/v1/opds/root.xml", 200)]
    [InlineData("/api/v1/opds/series/4", 404)]
    [InlineData("/api/v1/opds/chapter/9/page/1", 500)]
    public void Opds_never_reaches_the_log(string path, int status)
    {
        // The token is in the path and the log is kept on disk for days. This one outranks every
        // other tier, the 5xx included.
        Assert.Equal(LogEventLevel.Verbose, Level("GET", path, status));
    }

    [Fact]
    public void Server_errors_are_errors()
    {
        Assert.Equal(LogEventLevel.Error, Level("GET", "/api/v1/series", status: 500));
    }

    [Fact]
    public void A_throw_is_an_error_whatever_status_was_written()
    {
        Assert.Equal(LogEventLevel.Error, Level("GET", "/api/v1/series", exception: new InvalidOperationException()));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(409)]
    [InlineData(429)]
    public void Client_errors_the_server_refused_are_warnings(int status)
    {
        Assert.Equal(LogEventLevel.Warning, Level("POST", "/api/v1/series", status: status));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public void Unauthenticated_and_missing_are_ordinary_traffic(int status)
    {
        // A signed-out browser emits a row of 401s on its way to the login page, and a bookmarked
        // URL that no longer resolves is a 404. Warning-level for those trains the reader to skip
        // warnings.
        Assert.Equal(LogEventLevel.Debug, Level("GET", "/api/v1/auth/me", status: status));
    }

    [Fact]
    public void Mutations_are_the_record_of_who_changed_what()
    {
        Assert.Equal(LogEventLevel.Information, Level("POST", "/api/v1/series"));
        Assert.Equal(LogEventLevel.Information, Level("DELETE", "/api/v1/series/3"));
        Assert.Equal(LogEventLevel.Information, Level("PUT", "/api/v1/settings/library"));
        Assert.Equal(LogEventLevel.Information, Level("PATCH", "/api/v1/series/3"));
    }

    [Fact]
    public void Successful_reads_drop_to_debug()
    {
        // This is the polling traffic. No path list keeps up with the frontend's timers, so the
        // read/write split is what has to cover it.
        Assert.Equal(LogEventLevel.Debug, Level("GET", "/api/v1/queue"));
        Assert.Equal(LogEventLevel.Debug, Level("GET", "/api/v1/inbox/unread-count"));
        Assert.Equal(LogEventLevel.Debug, Level("GET", "/api/v1/system/health"));
    }

    [Theory]
    [InlineData("/api/v1/mediacover/1036/cover.jpg")]
    [InlineData("/api/v1/reader/chapter/12/page/3")]
    [InlineData("/api/v1/reader/chapter/12/thumb/3")]
    [InlineData("/assets/index-a91f.js")]
    [InlineData("/favicon.ico")]
    [InlineData("/index.html")]
    [InlineData("/signalr/events")]
    public void Bulk_reads_drop_below_debug(string path)
    {
        // One line per poster on screen and one per page turn. These have to stay out of the way
        // even when somebody has raised the level to Debug to chase something else.
        Assert.Equal(LogEventLevel.Verbose, Level("GET", path));
    }

    [Fact]
    public void A_bulk_read_that_404s_stays_below_debug()
    {
        // A series with no poster answers 404 once per tile on screen. Treating that as more
        // interesting than the 200 next to it puts the wall of lines straight back.
        Assert.Equal(LogEventLevel.Verbose, Level("GET", "/api/v1/mediacover/1/cover.jpg", status: 404));
        Assert.Equal(LogEventLevel.Verbose, Level("GET", "/favicon.ico", status: 404));
    }

    [Fact]
    public void Reader_reads_that_are_not_images_stay_at_debug()
    {
        Assert.Equal(LogEventLevel.Debug, Level("GET", "/api/v1/reader/chapter/12/bookmarks"));
        Assert.Equal(LogEventLevel.Debug, Level("GET", "/api/v1/reader/chapter/12"));
    }

    [Fact]
    public void A_slow_request_is_a_warning_even_when_it_succeeded()
    {
        Assert.Equal(LogEventLevel.Warning, Level("GET", "/api/v1/series", elapsedMs: 2500));
    }

    [Fact]
    public void A_slow_bulk_read_is_still_a_warning()
    {
        // The slow tier is checked before the read/write split precisely so this cannot be demoted:
        // a cover taking two seconds is the symptom worth seeing.
        Assert.Equal(LogEventLevel.Warning, Level("GET", "/api/v1/mediacover/1/cover.jpg", elapsedMs: 2500));
    }

    [Fact]
    public void Full_logs_every_request_at_information()
    {
        var full = Options with { HttpRequests = HttpRequestLogMode.Full };

        Assert.Equal(LogEventLevel.Information, Level("GET", "/api/v1/mediacover/1/cover.jpg", options: full));
        Assert.Equal(LogEventLevel.Information, Level("GET", "/api/v1/queue", options: full));

        // Not OPDS, and not a failure. Full raises the floor, it does not disable the tiers that
        // exist for a reason.
        Assert.Equal(LogEventLevel.Verbose, Level("GET", "/api/v1/opds/root.xml", options: full));
        Assert.Equal(LogEventLevel.Error, Level("GET", "/api/v1/series", status: 503, options: full));
    }

    [Fact]
    public void Off_keeps_failures_and_slow_requests_and_drops_the_rest()
    {
        var off = Options with { HttpRequests = HttpRequestLogMode.Off };

        Assert.Equal(LogEventLevel.Error, Level("GET", "/api/v1/series", status: 500, options: off));
        Assert.Equal(LogEventLevel.Warning, Level("POST", "/api/v1/series", status: 409, options: off));
        Assert.Equal(LogEventLevel.Warning, Level("GET", "/api/v1/series", elapsedMs: 2500, options: off));
        Assert.Equal(LogEventLevel.Verbose, Level("POST", "/api/v1/series", options: off));
        Assert.Equal(LogEventLevel.Verbose, Level("GET", "/api/v1/series", options: off));
    }
}
