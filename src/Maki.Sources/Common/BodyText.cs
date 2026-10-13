using AngleSharp.Html.Parser;

namespace Maki.Sources.Common;

internal static class BodyText
{
    private static readonly HtmlParser Parser = new();

    /// <summary>The text content of an HTML fragment; null for a blank one.</summary>
    public static string? Plain(string? html) =>
        string.IsNullOrWhiteSpace(html) ? null : Parser.ParseDocument(html).Body?.TextContent.Trim();

    /// <summary>The first 100 characters of a response body, for an error message.</summary>
    public static string Snippet(string body) => body.Length <= 100 ? body : body[..100];
}
