using AngleSharp.Dom;

namespace Maki.Sources.Toonily;

/// <summary>
/// Parsing helpers for the Madara WordPress theme (used by many manga/manhwa scan sites, Toonily
/// among them). Kept free of Toonily-specific constants so a future Madara site can reuse it as-is;
/// business logic that varies per site (status label mapping, promo-image filtering, date parsing)
/// stays in <c>ToonilySource</c>.
/// </summary>
internal static class MadaraParser
{
    public record ArchiveItem(string? PostId, string Href, string Title, string? CoverUrl);

    /// <summary>Search/archive result cards, as returned by admin-ajax and the GET search fallback alike.</summary>
    public static IReadOnlyList<ArchiveItem> ParseArchive(IDocument doc)
    {
        var items = new List<ArchiveItem>();
        foreach (var card in doc.QuerySelectorAll("div.page-item-detail, .c-tabs-item__content, .manga__item"))
        {
            var link = card.QuerySelector(".post-title a");
            var href = link?.GetAttribute("href");
            if (link is null || string.IsNullOrEmpty(href))
            {
                continue;
            }

            var postId = card.QuerySelector("[data-post-id]")?.GetAttribute("data-post-id");
            var cover = ImageUrl(card.QuerySelector("img"));
            items.Add(new ArchiveItem(postId, href, link.TextContent.Trim(), cover));
        }

        return items;
    }

    public record SeriesInfo(string? Title, string? CoverUrl, string? Description, string? Status);

    public static SeriesInfo ParseSeries(IDocument doc)
    {
        var titleEl = doc.QuerySelector("div.post-title h3, div.post-title h1, #manga-title > h1");
        var title = titleEl is null ? null : OwnText(titleEl);

        var cover = ImageUrl(doc.QuerySelector("div.summary_image img"));

        // "div.summary__content" alone covers Toonily's own nesting; the other two are Madara's
        // fallbacks for sites that wrap it differently.
        var description = doc
            .QuerySelectorAll(
                "div.description-summary div.summary__content, div.summary_content div.manga-excerpt, div.summary__content")
            .FirstOrDefault()
            ?.QuerySelector("p")
            ?.TextContent.Trim();

        string? status = null;
        foreach (var item in doc.QuerySelectorAll("div.post-content_item"))
        {
            var label = item.QuerySelector(".summary-heading h5")?.TextContent.Trim();
            if (!string.Equals(label, "Status", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            status = item.QuerySelector(".summary-content")?.TextContent.Trim();
            break;
        }

        return new SeriesInfo(
            string.IsNullOrEmpty(title) ? null : title,
            cover,
            string.IsNullOrEmpty(description) ? null : description,
            string.IsNullOrEmpty(status) ? null : status);
    }

    public record ChapterItem(string Href, string Name, string? DateText, string? VolumeRaw);

    /// <summary>
    /// Flat lists (Toonily's own <c>ul.main.version-chap.no-volumn</c>) and volume-grouped lists
    /// (<c>ul.sub-chap</c> under <c>li.parent.has-child &gt; a.has-child</c>) both work; a chapter
    /// li is never counted twice even if it matches both loops below.
    /// </summary>
    public static IReadOnlyList<ChapterItem> ParseChapters(IDocument doc)
    {
        var chapters = new List<ChapterItem>();
        var seen = new HashSet<IElement>();

        foreach (var parent in doc.QuerySelectorAll("li.parent.has-child"))
        {
            var volumeLabel = parent.Children
                .FirstOrDefault(c => c.TagName.Equals("A", StringComparison.OrdinalIgnoreCase)
                                      && c.ClassList.Contains("has-child"))
                ?.TextContent.Trim();

            foreach (var li in parent.QuerySelectorAll("ul.sub-chap li.wp-manga-chapter"))
            {
                if (!seen.Add(li))
                {
                    continue;
                }

                var item = ChapterFromLi(li, volumeLabel);
                if (item is not null)
                {
                    chapters.Add(item);
                }
            }
        }

        foreach (var li in doc.QuerySelectorAll("li.wp-manga-chapter"))
        {
            if (!seen.Add(li))
            {
                continue;
            }

            var item = ChapterFromLi(li, null);
            if (item is not null)
            {
                chapters.Add(item);
            }
        }

        return chapters;
    }

    private static ChapterItem? ChapterFromLi(IElement li, string? volumeRaw)
    {
        var link = li.QuerySelector("a");
        var href = link?.GetAttribute("href");
        if (link is null || string.IsNullOrEmpty(href))
        {
            return null;
        }

        var dateText = li.QuerySelector("span.chapter-release-date i")?.TextContent.Trim();
        if (string.IsNullOrEmpty(dateText))
        {
            dateText = li.QuerySelector("span.chapter-release-date a[title]")?.GetAttribute("title")?.Trim();
        }

        return new ChapterItem(
            href, link.TextContent.Trim(), string.IsNullOrEmpty(dateText) ? null : dateText, volumeRaw);
    }

    /// <summary>First non-empty of Madara's lazy-load attributes, the largest srcset candidate, then src.</summary>
    public static string? ImageUrl(IElement? img)
    {
        if (img is null)
        {
            return null;
        }

        foreach (var attr in new[] { "data-src", "data-lazy-src", "data-lzl-src", "data-cfsrc", "data-manga-src" })
        {
            var value = img.GetAttribute(attr);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        var srcset = img.GetAttribute("srcset");
        if (!string.IsNullOrWhiteSpace(srcset))
        {
            var best = srcset.Split(',')
                .Select(part => part.Trim().Split(' '))
                .Where(parts => parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]))
                .Select(parts => (Url: parts[0], Width: ParseWidth(parts)))
                .OrderByDescending(c => c.Width)
                .Select(c => c.Url)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(best))
            {
                return best.Trim();
            }
        }

        var src = img.GetAttribute("src");
        return string.IsNullOrWhiteSpace(src) ? null : src.Trim();
    }

    private static int ParseWidth(string[] parts) =>
        parts.Length > 1 && parts[1].EndsWith('w') && int.TryParse(parts[1][..^1], out var width) ? width : 0;

    /// <summary>Page images, unfiltered. Throws on Madara's AES chapter-protector variant (out of scope).</summary>
    public static IReadOnlyList<IElement> ParsePages(IDocument doc)
    {
        if (doc.QuerySelector("#chapter-protector-data") is not null)
        {
            throw new NotSupportedException(
                "Chapter is behind Madara's AES chapter-protector, which this parser does not support.");
        }

        return doc.QuerySelectorAll("div.reading-content div.page-break img").ToList();
    }

    private static string OwnText(IElement element) =>
        string.Concat(element.ChildNodes
                .Where(n => n.NodeType == NodeType.Text)
                .Select(n => n.TextContent))
            .Trim();
}
