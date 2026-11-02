using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Maki.Core.Sources.Custom;
using Maki.Sources.Common;

namespace Maki.Sources.Custom.Inference;

/// <summary>
/// Proposes a <see cref="CustomPages"/> section from a chapter page: the largest run of images
/// in the markup, or, for sites that only name their pages in a script payload, the largest set of
/// look-alike image paths in the raw HTML.
/// </summary>
public static partial class PageListGuesser
{
    public sealed record Guess(CustomPages Pages, IReadOnlyList<string> Images);

    private const int MinPages = 3;

    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp", ".avif", ".gif"];
    private static readonly string[] OtherExtensions = [".js", ".css", ".json", ".svg", ".ico", ".woff", ".woff2", ".ttf", ".map", ".html", ".php", ".xml"];

    /// <summary>Absolute or root-relative paths of two or more segments, with JSON-escaped slashes allowed.</summary>
    [GeneratedRegex(@"(?:https?:\\?/\\?/[A-Za-z0-9.-]+)?(?:\\?/[A-Za-z0-9._~%-]+){2,}")]
    private static partial Regex PathToken();

    [GeneratedRegex("(img|image|page|upload|cdn|media|scan|chapter)", RegexOptions.IgnoreCase)]
    private static partial Regex ImageishDirectory();

    public static Guess? Run(CustomPage chapter, CustomPage? series)
    {
        var known = series is null ? [] : KnownImages(series);
        return Markup(chapter, known) ?? Script(chapter, series);
    }

    private static Guess? Markup(CustomPage chapter, HashSet<string> known)
    {
        var groups = chapter.Document.QuerySelectorAll("img")
            .Where(img => img.Closest("header, nav, footer") is null)
            .Select(img => (Img: img, Url: ChapterListGuesser.Resolve(chapter.Url, MadaraParser.ImageUrl(img))))
            .Where(x => x.Url is not null && !known.Contains(x.Url.AbsoluteUri))
            .Select(x => (x.Img, Url: x.Url!, Repeated: SelectorBuilder.RepeatedAncestor(x.Img)))
            .Where(x => x.Repeated?.ParentElement is not null)
            .GroupBy(x => (x.Repeated!.ParentElement, SelectorBuilder.Signature(x.Repeated)))
            .Select(g => g.ToList())
            .Where(g => g.Count >= MinPages && g.Count(x => Directory(x.Url) == Directory(g[0].Url)) * 2 >= g.Count)
            .OrderByDescending(g => g.Count);

        foreach (var group in groups)
        {
            var repeated = group[0].Repeated!;
            if (SelectorBuilder.ForList(repeated) is not { } css)
            {
                continue;
            }

            var pages = new CustomPages { List = new ListExtractor { Css = repeated.LocalName == "img" ? css : css + " img" } };
            var images = Images(pages, chapter);
            if (images.Count >= MinPages)
            {
                return new Guess(pages, images);
            }
        }

        return null;
    }

    private static Guess? Script(CustomPage chapter, CustomPage? series)
    {
        var groups = PathToken().Matches(chapter.Body)
            .Select(m => m.Value.Replace("\\/", "/"))
            .Select(v => (Value: v, Dir: v[..(v.LastIndexOf('/') + 1)], Ext: Extension(v)))
            .Where(x => !OtherExtensions.Contains(x.Ext) && (ImageExtensions.Contains(x.Ext) || ImageishDirectory().IsMatch(x.Dir)))
            .Where(x => series is null || !series.Body.Contains(x.Value, StringComparison.Ordinal))
            .GroupBy(x => (x.Dir, x.Ext))
            .Select(g => (g.Key.Dir, g.Key.Ext, Count: g.Select(x => x.Value).Distinct().Count()))
            .Where(g => g.Count >= MinPages)
            .OrderByDescending(g => (ImageExtensions.Contains(g.Ext) ? 1 : 0, g.Count));

        foreach (var (dir, ext, _) in groups)
        {
            var pattern = Regex.Escape(dir).Replace("/", @"\\?/") + "[A-Za-z0-9_-]+" + Regex.Escape(ext);
            var pages = new CustomPages { List = new ListExtractor { Regex = pattern } };
            var images = Images(pages, chapter);
            if (images.Count >= MinPages)
            {
                return new Guess(pages, images);
            }
        }

        return null;
    }

    private static List<string> Images(CustomPages pages, CustomPage chapter) =>
        CustomExtractor.List(pages.List, chapter)
            .Select(CustomExtractor.DefaultImage)
            .Select(raw => ChapterListGuesser.Resolve(chapter.Url, raw)?.AbsoluteUri)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Take(CustomSource.MaxPages)
            .ToList();

    private static HashSet<string> KnownImages(CustomPage series) =>
        series.Document.QuerySelectorAll("img")
            .Select(img => ChapterListGuesser.Resolve(series.Url, MadaraParser.ImageUrl(img))?.AbsoluteUri)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

    private static string Directory(Uri url) => url.Host + url.AbsolutePath[..(url.AbsolutePath.LastIndexOf('/') + 1)];

    private static string Extension(string value)
    {
        var last = value[(value.LastIndexOf('/') + 1)..];
        var dot = last.LastIndexOf('.');
        return dot < 0 ? "" : last[dot..].ToLowerInvariant();
    }
}
