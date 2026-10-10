using AngleSharp.Html.Parser;

namespace Maki.Sources.Common;

/// <summary>
/// A direct fetch of a JSON endpoint returns the body as-is; through FlareSolverr the same body
/// comes back wrapped in an HTML document (the browser renders JSON inside a &lt;pre&gt;). Sources
/// that read JSON through <c>IHtmlFetcher</c> run the body through this before parsing it.
/// </summary>
internal static class PreUnwrap
{
    private static readonly HtmlParser Parser = new();

    public static async Task<string> UnwrapAsync(string body, string? url = null, CancellationToken ct = default)
    {
        var trimmed = body.TrimStart();
        if (!trimmed.StartsWith('<'))
        {
            return body;
        }

        var doc = await Parser.ParseDocumentAsync(body, ct);
        return PreOrThrow(doc.QuerySelector("pre")?.TextContent, trimmed, url);
    }

    public static string Unwrap(string body, string? url = null)
    {
        var trimmed = body.TrimStart();
        if (!trimmed.StartsWith('<'))
        {
            return body;
        }

        return PreOrThrow(Parser.ParseDocument(body).QuerySelector("pre")?.TextContent, trimmed, url);
    }

    private static string PreOrThrow(string? pre, string trimmed, string? url)
    {
        if (pre is not null)
        {
            return pre;
        }

        // An HTML body with no <pre> is neither a direct JSON response nor FlareSolverr's usual
        // wrapper. Returning it as-is would fail later as a cryptic JsonException from the caller.
        var snippet = trimmed.Length > 100 ? trimmed[..100] : trimmed;
        throw new InvalidOperationException(
            $"Response for {url ?? "(unknown URL)"} looks like HTML with no <pre> to unwrap: \"{snippet}\"");
    }
}
