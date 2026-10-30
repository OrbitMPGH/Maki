using System.Text.RegularExpressions;

namespace Maki.Sources.Custom.Inference;

/// <summary>URL templates read off real links: which path segments stay fixed and which carry an id.</summary>
public static class UrlShape
{
    /// <summary>The series page's template, with its last path segment as <c>{seriesId}</c>.</summary>
    public static (string Template, string SeriesId)? Series(Uri seriesUrl)
    {
        var segments = seriesUrl.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || seriesUrl.Query.Length > 0)
        {
            return null;
        }

        var trailing = seriesUrl.AbsolutePath.EndsWith('/') ? "/" : "";
        var template = "/" + string.Join("/", segments[..^1].Append(CustomTemplate.SeriesId)) + trailing;
        return (template, Uri.UnescapeDataString(segments[^1]));
    }

    /// <summary>A chapter template, the regex that reads its id back out of a link's path, and each link's id (null where it doesn't fit).</summary>
    public sealed record Shape(string Template, string IdRegex, IReadOnlyList<string?> Ids);

    /// <summary>
    /// Segments that are the same in every link stay literal, a segment equal to the series id
    /// becomes <c>{seriesId}</c>, and the one run of segments that changes is <c>{chapterId}</c>.
    /// Links with a query string or another segment count than most don't fit.
    /// </summary>
    public static Shape? Chapters(IReadOnlyList<Uri> links, string seriesId)
    {
        var split = links.Select(l => l.Query.Length > 0 ? null : l.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)).ToList();
        var length = split.Where(s => s is { Length: > 0 }).GroupBy(s => s!.Length).MaxBy(g => g.Count())?.Key;
        if (length is not { } n)
        {
            return null;
        }

        var fitting = split.Where(s => s?.Length == n).Select(s => s!).ToList();
        var kinds = new Kind[n];
        for (var i = 0; i < n; i++)
        {
            var values = fitting.Select(s => Uri.UnescapeDataString(s[i])).Distinct(StringComparer.Ordinal).ToList();
            kinds[i] = values.Count == 1 && fitting.Count > 1
                ? values[0] == seriesId ? Kind.SeriesId : Kind.Literal
                : Kind.Varying;
        }

        // One link alone can't show what changes: its series-id segment is the series, its last is the chapter.
        if (fitting.Count == 1)
        {
            for (var i = 0; i < n; i++)
            {
                kinds[i] = i == n - 1 ? Kind.Varying : Uri.UnescapeDataString(fitting[0][i]) == seriesId ? Kind.SeriesId : Kind.Literal;
            }
        }

        var first = Array.IndexOf(kinds, Kind.Varying);
        var last = Array.LastIndexOf(kinds, Kind.Varying);
        if (first < 0 || kinds[first..(last + 1)].Any(k => k != Kind.Varying))
        {
            return null;
        }

        var trailing = links.Count(l => l.AbsolutePath.EndsWith('/')) * 2 >= links.Count ? "/" : "";
        var template = new List<string>();
        var regex = new List<string>();
        for (var i = 0; i < n; i++)
        {
            if (i == first)
            {
                template.Add(CustomTemplate.ChapterId);
                regex.Add("(" + string.Join("/", Enumerable.Repeat("[^/]+", last - first + 1)) + ")");
                i = last;
                continue;
            }

            template.Add(kinds[i] == Kind.SeriesId ? CustomTemplate.SeriesId : fitting[0][i]);
            regex.Add(kinds[i] == Kind.SeriesId ? "[^/]+" : Regex.Escape(fitting[0][i]));
        }

        var ids = split
            .Select(s => s?.Length == n ? Uri.UnescapeDataString(string.Join("/", s[first..(last + 1)])) : null)
            .ToList();
        return new Shape("/" + string.Join("/", template) + trailing, "^/" + string.Join("/", regex) + "/?$", ids);
    }

    private enum Kind { Literal, SeriesId, Varying }
}
