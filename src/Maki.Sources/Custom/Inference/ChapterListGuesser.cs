using System.Globalization;
using AngleSharp.Dom;
using Maki.Core.Parsing;
using Maki.Core.Sources.Custom;
using Maki.Sources.Common;

namespace Maki.Sources.Custom.Inference;

/// <summary>
/// Proposes a <see cref="CustomChapters"/> section from a series page: which repeated element is a
/// chapter, how its id and URL relate, and where its number and date sit.
/// </summary>
public static class ChapterListGuesser
{
    public sealed record Row(string? Id, string? Text, decimal? Number, DateTime? Date, string? Link);

    /// <param name="FromSample">The list holds the chapter URL the admin pasted, which makes it far more likely to be the right one.</param>
    public sealed record Guess(CustomChapters Chapters, int Count, int Numbered, bool FromSample, IReadOnlyList<Row> Rows);

    private const int MaxGroups = 8;

    public static IReadOnlyList<Guess> Run(CustomPage series, string seriesId, params Uri?[] chapterUrls)
    {
        var anchors = series.Document.QuerySelectorAll("a[href]")
            .Select(a => (Element: a, Link: Resolve(series.Url, a.GetAttribute("href"))))
            .Where(x => x.Link is not null && CustomHost.Same(x.Link.Host, series.Url.Host))
            .Select(x => (x.Element, Link: x.Link!))
            .ToList();

        var candidates = new List<(IElement Item, bool FromSample)>();
        foreach (var target in chapterUrls.OfType<Uri>())
        {
            foreach (var (element, _) in anchors.Where(x => SamePath(x.Link, target)))
            {
                if (SelectorBuilder.RepeatedAncestor(element) is { } item)
                {
                    candidates.Add((item, true));
                }
            }
        }

        // Any group of look-alike links where most carry a digit somewhere: the parser only knows a
        // few languages' words for "chapter", and Build checks the rest.
        var groups = anchors
            .GroupBy(x => Chain(x.Element))
            .Where(g => g.Count() >= 2 && g.Count(x => (x.Element.TextContent + x.Link.AbsolutePath).Any(char.IsAsciiDigit)) * 5 >= g.Count() * 3)
            .OrderByDescending(g => g.Count())
            .Take(MaxGroups);
        foreach (var group in groups)
        {
            if (SelectorBuilder.RepeatedAncestor(group.First().Element) is { } item)
            {
                candidates.Add((item, false));
            }
        }

        var guesses = new List<Guess>();
        var tried = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (item, fromSample) in candidates)
        {
            if (SelectorBuilder.ForList(item) is not { } css || !tried.Add(css))
            {
                continue;
            }

            if (Build(series, seriesId, css, fromSample) is { } guess)
            {
                guesses.Add(guess);
            }
        }

        // A chapter list usually links under the series' own path or slug, which a list of other
        // series' latest chapters does not.
        return guesses
            .OrderByDescending(g => g.FromSample)
            .ThenByDescending(g => g.Rows.Count(r => r.Link?.Contains(seriesId, StringComparison.OrdinalIgnoreCase) == true) * 2 >= g.Count)
            .ThenByDescending(g => g.Numbered)
            .ThenByDescending(g => g.Count)
            .Take(3)
            .ToList();
    }

    private static Guess? Build(CustomPage series, string seriesId, string css, bool fromSample)
    {
        var items = series.Document.QuerySelectorAll(css).ToList();
        var links = items.Select(i => Resolve(series.Url, new CustomItem(i, null, null).Href)).ToList();
        var onHost = links.Where(l => l is not null && CustomHost.Same(l.Host, series.Url.Host)).Select(l => l!).ToList();
        if (onHost.Count == 0 || UrlShape.Chapters(onHost, seriesId) is not { } shape)
        {
            return null;
        }

        // Every link must come back out of the template, or downloads would rebuild a wrong URL.
        var baseUri = new Uri(series.Url.GetLeftPart(UriPartial.Authority) + "/");
        var roundTrips = onHost.Zip(shape.Ids)
            .Count(x => x.Second is not null && SamePath(CustomTemplate.Expand(baseUri, shape.Template, seriesId, x.Second), x.First));
        if (roundTrips * 10 < onHost.Count * 9)
        {
            return null;
        }

        var date = DateField(items);
        var number = NumberField(items, links);

        var anchor = items[0].LocalName == "a";
        var chapters = new CustomChapters
        {
            Item = new ListExtractor { Css = css },
            Id = new FieldExtractor { Css = anchor ? null : "a[href]", Attr = "href", Regex = shape.IdRegex },
            Url = shape.Template,
            Number = number,
            Date = date
        };

        var rows = new List<Row>();
        var ids = new Queue<string?>(shape.Ids);
        foreach (var (element, link) in items.Zip(links))
        {
            var id = link is not null && CustomHost.Same(link.Host, series.Url.Host) && ids.Count > 0 ? ids.Dequeue() : null;
            var item = new CustomItem(element, null, null);
            var text = CustomExtractor.Field(number ?? new FieldExtractor(), item);
            rows.Add(new Row(id, Clean(text), NumberOf(text, link), DateOf(CustomExtractor.Field(date, item), date), link?.AbsoluteUri));
        }

        var numbered = rows.Count(r => r.Number is not null);
        if (!fromSample && numbered * 2 < rows.Count)
        {
            return null;
        }

        return new Guess(chapters, rows.Count, numbered, fromSample, rows);
    }

    /// <summary>
    /// One of the item's descendants or its whole text, read by the parser or for its first or last
    /// number, whichever gives the most different numbers across the list. Chapter numbers differ
    /// from row to row; a year, a view count or a "new" badge repeats.
    /// </summary>
    private static FieldExtractor? NumberField(IReadOnlyList<IElement> items, IReadOnlyList<Uri?> links)
    {
        // The item's whole text comes last on a tie: its elements' texts run together ("Ch. 2" and
        // "1 month" read as "Ch. 21 month").
        var keys = Descendants(items).Where(k => !items.Any(i => i.QuerySelector(k)?.TextContent is { } t && LooksLikeDate(t)))
            .Cast<string?>()
            .Append(null);
        var candidates = keys
            .SelectMany(key => new[] { null, FirstNumber, LastNumber }.Select(regex => key is null && regex is null ? null : new FieldExtractor { Css = key, Regex = regex }))
            .Select((field, order) => (Field: field, Order: order, Distinct: items.Zip(links)
                .Select(x => NumberOf(CustomExtractor.Field(field ?? new FieldExtractor(), new CustomItem(x.First, null, null)), x.Second))
                .Where(n => n is not null)
                .Distinct()
                .Count()))
            .ToList();
        return candidates.OrderByDescending(c => c.Distinct).ThenBy(c => c.Order).First().Field;
    }

    private static DateExtractor? DateField(IReadOnlyList<IElement> items)
    {
        if (items.Count(i => i.QuerySelector("time[datetime]") is not null) * 5 >= items.Count * 3)
        {
            return new DateExtractor { Css = "time", Attr = "datetime", Format = "iso" };
        }

        foreach (var key in Descendants(items))
        {
            var texts = items.Select(i => i.QuerySelector(key)?.TextContent.Trim()).ToList();
            var relative = texts.Count(t => RelativeDate.Parse(t) is not null);
            var absolute = texts.Count(t => t is not null && RelativeDate.Parse(t) is null && Absolute(t) is not null);
            if ((relative + absolute) * 5 >= items.Count * 3)
            {
                return new DateExtractor { Css = key, Format = relative >= absolute ? "relative" : "iso" };
            }
        }

        return null;
    }

    /// <summary>Selectors for the elements inside the first item that also exist in most of the others.</summary>
    private static IEnumerable<string> Descendants(IReadOnlyList<IElement> items) =>
        items[0].QuerySelectorAll("*")
            .Take(30)
            .Where(e => e.Children.Length == 0 && !string.IsNullOrWhiteSpace(e.TextContent))
            .Select(SelectorBuilder.Relative)
            .Distinct(StringComparer.Ordinal)
            .Where(key => items.Count(i => i.QuerySelector(key) is not null) * 5 >= items.Count * 4);

    /// <summary>For labels like "Capitolo 12" or "الفصل 12" that the parser has no word for.</summary>
    private const string FirstNumber = @"(\d+(?:\.\d+)?)";
    private const string LastNumber = @"(\d+(?:\.\d+)?)\D*$";

    private static bool LooksLikeDate(string text) => RelativeDate.Parse(text) is not null || Absolute(text) is not null;

    private static DateTime? Absolute(string text) =>
        text.Any(char.IsLetter) || text.Count(c => c is '-' or '/' or '.') >= 2
            ? DateTime.TryParse(text.Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date) ? date : null
            : null;

    private static DateTime? DateOf(string? text, DateExtractor? date) =>
        text is null || date is null ? null : date.Format == "relative" ? RelativeDate.Parse(text) : Absolute(text);

    private static decimal? NumberOf(string? text, Uri? link) =>
        ChapterNumberParser.Parse(Clean(text)).OrSlugNumber(link?.AbsoluteUri).Number;

    /// <summary>The ancestry of a link, three levels up, so links in the same kind of list group together.</summary>
    private static string Chain(IElement a)
    {
        var parts = new List<string>();
        for (IElement? e = a; e is not null && parts.Count < 4; e = e.ParentElement)
        {
            parts.Add(SelectorBuilder.Signature(e));
        }

        return string.Join(" < ", parts);
    }

    private static string? Clean(string? text) =>
        text is null ? null : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static Uri? Resolve(Uri page, string? raw) =>
        !string.IsNullOrWhiteSpace(raw) && Uri.TryCreate(page, raw.Trim(), out var uri) && uri.Scheme is "http" or "https" ? uri : null;

    public static bool SamePath(Uri a, Uri b) =>
        CustomHost.Same(a.Host, b.Host)
        && string.Equals(a.AbsolutePath.TrimEnd('/'), b.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal)
        && a.Query == b.Query;
}
