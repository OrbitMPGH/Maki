using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Maki.Core.Sources.Custom;

namespace Maki.Sources.Custom.Inference;

/// <summary>
/// Finds how a site searches by trying its own search form, then the common shapes, with the
/// sample series' title as the query. A shape counts only when its results link the sample series.
/// Failing that, a catalogue page that lists it.
/// </summary>
public static class SearchGuesser
{
    public sealed record Attempt(string Url, bool Found, string? Error);

    public sealed record Guess(CustomSearch? Search, CustomCatalogue? Catalogue, int Results, IReadOnlyList<string> Titles, IReadOnlyList<Attempt> Tried);

    /// <summary>A fetched page, or the reason it couldn't be fetched.</summary>
    public delegate Task<(CustomPage? Page, string? Error)> Fetch(Uri url, CancellationToken ct);

    public static readonly string[] CommonShapes = ["/?s={query}", "/search?q={query}", "/search/?q={query}", "/search?keyword={query}"];
    public static readonly string[] CatalogueShapes = ["/series/", "/manga/", "/comics/"];

    private static readonly string[] QueryNames = ["s", "q", "keyword", "query", "search"];

    public static async Task<Guess> RunAsync(Uri baseUri, string seriesTemplate, string seriesId, string query, Fetch fetch, CancellationToken ct)
    {
        var tried = new List<Attempt>();
        var (home, homeError) = await fetch(baseUri, ct);
        var templates = new List<string>();
        if (home is not null && FormTemplate(home) is { } form)
        {
            templates.Add(form);
        }

        templates.AddRange(CommonShapes);
        foreach (var template in templates.Distinct(StringComparer.Ordinal))
        {
            var url = CustomTemplate.Expand(baseUri, template, query: query);
            var (page, error) = await fetch(url, ct);
            var list = page is null ? null : SeriesList(page, baseUri, seriesTemplate, seriesId, query);
            tried.Add(new Attempt(url.AbsoluteUri, list is not null, error));
            if (list is not null)
            {
                return new Guess(
                    new CustomSearch { Url = template, Item = list.Item, Id = list.Id, Title = list.Title, Cover = list.Cover },
                    null, list.Count, list.Titles, tried);
            }
        }

        foreach (var template in CatalogueShapes)
        {
            var url = CustomTemplate.Expand(baseUri, template);
            var (page, error) = await fetch(url, ct);
            var list = page is null ? null : SeriesList(page, baseUri, seriesTemplate, seriesId, query);
            tried.Add(new Attempt(url.AbsoluteUri, list is not null, error));
            if (list is not null)
            {
                var next = page!.Document.QuerySelector("a[rel='next'][href]") is not null
                    ? new FieldExtractor { Css = "a[rel='next']", Attr = "href" }
                    : null;
                return new Guess(
                    null,
                    new CustomCatalogue { Url = template, Item = list.Item, Id = list.Id, Title = list.Title, Cover = list.Cover, NextPage = next },
                    list.Count, list.Titles, tried);
            }
        }

        if (home is null)
        {
            tried.Insert(0, new Attempt(baseUri.AbsoluteUri, false, homeError));
        }

        return new Guess(null, null, 0, [], tried);
    }

    /// <summary>A GET form on the home page with a search box, as a template that keeps its hidden fields.</summary>
    public static string? FormTemplate(CustomPage home)
    {
        foreach (var form in home.Document.QuerySelectorAll("form").OfType<IHtmlFormElement>())
        {
            if (!string.Equals(form.Method, "get", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var inputs = form.QuerySelectorAll("input[name]").OfType<IHtmlInputElement>().ToList();
            var box = inputs.FirstOrDefault(i => i.Type == "search") ?? inputs.FirstOrDefault(i => QueryNames.Contains(i.Name));
            if (box?.Name is not { Length: > 0 } name)
            {
                continue;
            }

            var action = ChapterListGuesser.Resolve(home.Url, form.GetAttribute("action") is { Length: > 0 } a ? a : home.Url.AbsoluteUri);
            if (action is null || !CustomHost.Same(action.Host, home.Url.Host))
            {
                continue;
            }

            var fields = inputs
                .Where(i => i != box && i.Type == "hidden" && !string.IsNullOrEmpty(i.Name))
                .Select(i => $"{Uri.EscapeDataString(i.Name!)}={Uri.EscapeDataString(i.Value)}")
                .Prepend($"{Uri.EscapeDataString(name)}={CustomTemplate.Query}");
            return action.AbsolutePath + "?" + string.Join("&", fields);
        }

        return null;
    }

    private sealed record Found(ListExtractor Item, FieldExtractor Id, FieldExtractor? Title, FieldExtractor? Cover, int Count, IReadOnlyList<string> Titles);

    private static Found? SeriesList(CustomPage page, Uri baseUri, string seriesTemplate, string seriesId, string query)
    {
        var anchors = page.Document.QuerySelectorAll("a[href]")
            .Where(a => ChapterListGuesser.Resolve(page.Url, a.GetAttribute("href")) is { } link
                && CustomTemplate.Match(baseUri, seriesTemplate, link, CustomTemplate.SeriesId) == seriesId);

        var idRegex = IdRegex(seriesTemplate);
        foreach (var anchor in anchors)
        {
            var item = SelectorBuilder.RepeatedAncestor(anchor) ?? Card(anchor);
            if (SelectorBuilder.ForList(item) is not { } css)
            {
                continue;
            }

            var items = page.Document.QuerySelectorAll(css).ToList();
            var isAnchor = item == anchor;
            var link = isAnchor ? null : SelectorBuilder.Relative(anchor) + "[href]";
            var id = new FieldExtractor { Css = link, Attr = "href", Regex = idRegex };
            var ids = items.Select(i => CustomExtractor.Narrow(Path(page, CustomExtractor.Field(id with { Regex = null }, new CustomItem(i, null, null))), idRegex)).ToList();
            if (ids.Count(x => x is not null) * 2 < items.Count)
            {
                continue;
            }

            var title = TitleField(item, anchor, query);
            var cover = CoverField(item);
            var titles = items.Select(i => CustomExtractor.Field(title ?? new FieldExtractor(), new CustomItem(i, null, null))?.Trim())
                .OfType<string>().Where(t => t.Length > 0).Take(5).ToList();
            return new Found(new ListExtractor { Css = css }, id, title, cover, items.Count, titles);
        }

        return null;
    }

    /// <summary>A lone result can't show what repeats, so take the closest box around its link that holds a cover.</summary>
    private static IElement Card(IElement anchor)
    {
        var e = anchor;
        for (var i = 0; i < 4 && e.QuerySelector("img") is null && e.ParentElement is { TagName: not ("BODY" or "MAIN") } parent; i++)
        {
            e = parent;
        }

        return e.QuerySelector("img") is not null ? e : anchor;
    }

    private static FieldExtractor? TitleField(IElement item, IElement anchor, string query)
    {
        if (anchor.GetAttribute("title") is { Length: > 0 })
        {
            return new FieldExtractor { Css = item == anchor ? null : SelectorBuilder.Relative(anchor), Attr = "title" };
        }

        var words = Regex.Split(query.ToLowerInvariant(), @"\W+").Where(w => w.Length > 2).ToList();
        var best = item.QuerySelectorAll("*")
            .Where(e => e.Children.Length == 0 && e.TextContent.Trim().Length > 0)
            .MaxBy(e => words.Count(w => e.TextContent.Contains(w, StringComparison.OrdinalIgnoreCase)));
        if (best is not null && words.Count > 0 && words.Count(w => best.TextContent.Contains(w, StringComparison.OrdinalIgnoreCase)) * 2 >= words.Count)
        {
            return new FieldExtractor { Css = SelectorBuilder.Relative(best) };
        }

        return item == anchor ? null : new FieldExtractor { Css = SelectorBuilder.Relative(anchor) };
    }

    private static FieldExtractor? CoverField(IElement item)
    {
        if (item.QuerySelector("img") is not { } img)
        {
            return null;
        }

        var attr = new[] { "data-src", "data-lazy-src", "src" }.FirstOrDefault(a => !string.IsNullOrWhiteSpace(img.GetAttribute(a)));
        return attr is null ? null : new FieldExtractor { Css = "img", Attr = attr };
    }

    /// <summary>The series template as a pattern over a link's path, with the id as group 1.</summary>
    public static string IdRegex(string seriesTemplate)
    {
        var path = seriesTemplate.TrimEnd('/');
        var at = path.IndexOf(CustomTemplate.SeriesId, StringComparison.Ordinal);
        return "^" + Regex.Escape(path[..at]) + "([^/]+)" + Regex.Escape(path[(at + CustomTemplate.SeriesId.Length)..]) + "/?$";
    }

    private static string? Path(CustomPage page, string? href) =>
        ChapterListGuesser.Resolve(page.Url, href) is { } uri && CustomHost.Same(uri.Host, page.Url.Host) ? uri.AbsolutePath : null;
}
