using System.Globalization;
using System.Net;
using System.Text;
using Maki.Api.Services;
using Serilog.Core;
using Serilog.Events;

namespace Maki.Api.Logging;

/// <summary>
/// Writes one line per request to <c>logs/access-*.log</c> when <c>AccessLog</c> is on in
/// config.json, so an internet-facing instance can sit behind CrowdSec or fail2ban
/// (<c>distribution/crowdsec</c>). Every request is written, whatever <c>HttpRequestLogging</c> says:
/// a ban tool needs the successful lines too, to tell a reader paging through a chapter from a scan.
/// <para>
/// Registered after <c>UseForwardedHeaders</c> so the address is the client's, not the proxy's, and
/// outside the exception handler so a throw still gets its line.
/// </para>
/// </summary>
public sealed class AccessLogMiddleware(RequestDelegate next, AccessLogWriter writer)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = DateTimeOffset.Now;
        // Read before the pipeline runs: the SPA fallback rewrites Request.Path to /index.html, which
        // would log every probe for /wp-login.php or /.env as a request for the shell.
        var target = AccessLogLine.Target(context.Request);
        var failed = true;
        try
        {
            await next(context);
            failed = false;
        }
        finally
        {
            writer.Write(AccessLogLine.Format(context, started, failed, target));
        }
    }
}

/// <summary>Owns the access log's file sink for the life of the host.</summary>
public sealed class AccessLogWriter(Logger logger) : IDisposable
{
    public void Write(string line) => logger.Write(LogEventLevel.Information, "{Line:l}", line);

    public void Dispose() => logger.Dispose();
}

/// <summary>
/// Builds a line in the combined log format nginx and Apache write, so CrowdSec's and fail2ban's
/// stock HTTP parsers read it as is:
/// <c>203.0.113.7 - alice [30/Oct/2026:14:02:11 +0100] "POST /api/v1/auth/login HTTP/1.1" 401 0 "-" "Mozilla/5.0"</c>.
/// <para>
/// The user field is the signed-in account (<c>-</c> when anonymous), or <c>opds-user-{id}</c> for
/// a request authenticated by an OPDS or Komga token. The byte count is the response's
/// Content-Length, so a compressed or streamed response shows 0.
/// </para>
/// <para>
/// Nothing that authenticates reaches the file: the OPDS token in the path and the values of the
/// query parameters in <see cref="SensitiveParameters"/> (the OIDC callback's <c>code</c> and
/// <c>state</c> among them) are replaced before the line is built.
/// </para>
/// </summary>
public static class AccessLogLine
{
    internal const string Redacted = "redacted";

    private static readonly HashSet<string> SensitiveParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "code", "state", "session_state", "token", "access_token", "id_token", "apikey", "api_key", "password",
    };

    /// <summary>Status for a request the client gave up on before anything was sent, as nginx logs it.</summary>
    internal const int ClientClosedRequest = 499;

    public static string Format(HttpContext context, DateTimeOffset at, bool failed = false, string? target = null)
    {
        var request = context.Request;
        var response = context.Response;

        // A throw that escaped the pipeline before anything was sent is answered 500 by Kestrel,
        // while the status still reads whatever the default was. A throw caused by the client going
        // away is not a server error, and logging it as one would get a reader skipping pages banned.
        var status = !failed || response.HasStarted
            ? response.StatusCode
            : context.RequestAborted.IsCancellationRequested
                ? ClientClosedRequest
                : StatusCodes.Status500InternalServerError;
        var protocol = string.IsNullOrEmpty(request.Protocol) ? "HTTP/1.1" : request.Protocol;

        var line = new StringBuilder(256);
        line.Append(ClientAddress(context.Connection.RemoteIpAddress))
            .Append(" - ")
            .Append(UserField(context))
            .Append(" [")
            .Append(Timestamp(at))
            .Append("] \"")
            .Append(Escape(request.Method))
            .Append(' ')
            .Append(Escape(target ?? Target(request)))
            .Append(' ')
            .Append(Escape(protocol))
            .Append("\" ")
            .Append(status.ToString(CultureInfo.InvariantCulture))
            .Append(' ')
            .Append((response.ContentLength ?? 0).ToString(CultureInfo.InvariantCulture))
            .Append(" \"")
            .Append(Escape(Referer(request.Headers.Referer.ToString())))
            .Append("\" \"")
            .Append(Escape(OrDash(request.Headers.UserAgent.ToString())))
            .Append('"');
        return line.ToString();
    }

    private static string ClientAddress(IPAddress? address)
    {
        if (address is null)
            return "-";

        // Kestrel listening on * is dual-stack, so an IPv4 client arrives as ::ffff:a.b.c.d. A ban
        // on that form would never match the address the firewall sees.
        return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    }

    private static string UserField(HttpContext context)
    {
        string? name = null;
        if (context.User.Identity is { IsAuthenticated: true, Name: { Length: > 0 } signedIn })
            name = signedIn;
        else if (context.Items.TryGetValue(typeof(OpdsAccess), out var item) && item is OpdsAccess access)
            name = $"opds-user-{access.UserId.ToString(CultureInfo.InvariantCulture)}";

        return name is null ? "-" : EncodeUser(name);
    }

    /// <summary>
    /// Percent-encodes everything outside the characters nginx's parsers accept in the user field
    /// (<c>[A-Za-z0-9.@+_-]</c>), so a username with a space or a quote cannot break the line. A
    /// username that is a lone dash would read as anonymous, so that one is encoded too.
    /// </summary>
    private static string EncodeUser(string name)
    {
        if (name == "-")
            return "%2D";

        var encoded = new StringBuilder(name.Length);
        foreach (var b in Encoding.UTF8.GetBytes(name))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '@' or '+' or '_' or '-')
                encoded.Append(c);
            else
                encoded.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }

        return encoded.ToString();
    }

    private static string Timestamp(DateTimeOffset at)
    {
        var offset = at.Offset;
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        var abs = offset.Duration();
        return at.ToString("dd/MMM/yyyy:HH:mm:ss", CultureInfo.InvariantCulture)
            + $" {sign}{abs.Hours:00}{abs.Minutes:00}";
    }

    public static string Target(HttpRequest request)
    {
        var path = request.Path;
        if (path.StartsWithSegments(HttpRequestLogPolicy.OpdsPrefix, out var rest))
        {
            // /api/v1/opds/{token}/rest: keep the rest so a scan is still visible, drop the token.
            var remainder = rest.Value ?? "";
            var afterToken = remainder.Length > 1 ? remainder.IndexOf('/', 1) : -1;
            path = remainder.Length == 0
                ? path
                : new PathString($"{HttpRequestLogPolicy.OpdsPrefix}/{Redacted}{(afterToken < 0 ? "" : remainder[afterToken..])}");
        }

        return request.PathBase.Add(path).ToUriComponent() + RedactQuery(request.QueryString.Value);
    }

    private static string Referer(string referer)
    {
        if (referer.Length == 0)
            return "-";

        var query = referer.IndexOf('?');
        return query < 0 ? referer : referer[..query] + RedactQuery(referer[query..]);
    }

    /// <summary>Takes the query with its leading <c>?</c> and masks the values of sensitive parameters.</summary>
    private static string RedactQuery(string? query)
    {
        if (string.IsNullOrEmpty(query) || query.Length == 1)
            return query ?? "";

        var parts = query[1..].Split('&');
        for (var i = 0; i < parts.Length; i++)
        {
            var equals = parts[i].IndexOf('=');
            var name = equals < 0 ? parts[i] : parts[i][..equals];
            if (equals >= 0 && SensitiveParameters.Contains(Uri.UnescapeDataString(name)))
                parts[i] = $"{name}={Redacted}";
        }

        return "?" + string.Join('&', parts);
    }

    private static string OrDash(string value) => value.Length == 0 ? "-" : value;

    /// <summary>
    /// The fields are quoted, and the parsers read a quoted field as "anything but a double quote",
    /// so quotes, backslashes and control characters are written as <c>\xHH</c>, the way nginx does.
    /// </summary>
    private static string Escape(string value)
    {
        var clean = true;
        foreach (var c in value)
        {
            if (c is '"' or '\\' || char.IsControl(c))
            {
                clean = false;
                break;
            }
        }

        if (clean)
            return value;

        var escaped = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (c is '"' or '\\' || char.IsControl(c))
                escaped.Append("\\x").Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
            else
                escaped.Append(c);
        }

        return escaped.ToString();
    }
}
