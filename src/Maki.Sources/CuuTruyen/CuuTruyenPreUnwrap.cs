using AngleSharp.Html.Parser;

namespace Maki.Sources.CuuTruyen;

/// <summary>
/// A direct fetch of /api/v2/* returns the JSON body as-is; through FlareSolverr the same body
/// comes back wrapped in an HTML document (the browser renders JSON inside a &lt;pre&gt;). 00-GENERAL
/// points at MangaFireSource for this helper, but that source is Playwright-only now, so it's
/// reimplemented here.
/// </summary>
internal static class CuuTruyenPreUnwrap
{
    private static readonly HtmlParser Parser = new();

    public static async Task<string> UnwrapAsync(string body, CancellationToken ct = default)
    {
        var trimmed = body.TrimStart();
        if (!trimmed.StartsWith('<'))
        {
            return body;
        }

        var doc = await Parser.ParseDocumentAsync(body, ct);
        return doc.QuerySelector("pre")?.TextContent ?? body;
    }
}
