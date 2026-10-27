using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Maki.Core.Sources;
using Maki.Core.Sources.Custom;

namespace Maki.Sources.Custom;

/// <summary>One thing wrong with a definition, as a catalogue key the API words for the reader.</summary>
public sealed record CustomSourceProblem(string Key, object? Args = null)
{
    public override string ToString() => Args is null ? Key : $"{Key} {Args}";
}

/// <summary>
/// Everything that can be checked about a definition without fetching anything: the schema, ids,
/// hosts, templates, and that every selector and pattern compiles. The live test run is
/// <c>CustomSourceTester</c>'s job.
/// </summary>
public static partial class CustomSourceValidator
{
    public const int MaxDefinitionBytes = 32 * 1024;
    public const int MinIntervalMs = 250;
    public const int MaxIntervalMs = 60_000;

    private static readonly HashSet<string> RefusedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Cookie", "Content-Length", "Content-Type", "Connection", "Keep-Alive", "Transfer-Encoding",
        "Upgrade", "TE", "Trailer", "Proxy-Authorization", "Proxy-Connection"
    };

    private static readonly HtmlParser Parser = new();
    private static readonly IDocument Empty = Parser.ParseDocument("<html><body></body></html>");

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,39}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$", RegexOptions.IgnoreCase)]
    private static partial Regex HostPattern();

    public static List<CustomSourceProblem> Validate(CustomSourceDefinition d)
    {
        var problems = new List<CustomSourceProblem>();
        void Add(string key, object? args = null) => problems.Add(new CustomSourceProblem(key, args));

        if (d.Schema != CustomSourceDefinition.CurrentSchema)
        {
            Add("error.customSource.schemaUnsupported", new { schema = d.Schema });
            return problems;
        }

        if (!IdPattern().IsMatch(d.Id))
        {
            Add("error.customSource.idInvalid");
        }

        if (string.IsNullOrWhiteSpace(d.Name) || d.Name.Length > 60)
        {
            Add("error.customSource.nameInvalid");
        }

        var baseUri = BaseUri(d.BaseUrl);
        if (baseUri is null)
        {
            Add("error.customSource.baseUrlInvalid");
        }

        var language = SourceLanguages.Canonical(d.Language);
        if (string.IsNullOrWhiteSpace(language) || language is "all" or "multi" or "other")
        {
            Add("error.customSource.languageInvalid");
        }

        if (CustomSourceEnums.Kind(d.Kind) is null)
        {
            Add("error.customSource.fieldInvalid", new { field = "kind" });
        }

        if (CustomSourceEnums.Content(d.Content) is null)
        {
            Add("error.customSource.fieldInvalid", new { field = "content" });
        }

        if (CustomSourceEnums.Rating(d.Rating) is null)
        {
            Add("error.customSource.fieldInvalid", new { field = "rating" });
        }

        if (d.MinIntervalMs is < MinIntervalMs or > MaxIntervalMs)
        {
            Add("error.customSource.intervalInvalid", new { min = MinIntervalMs, max = MaxIntervalMs });
        }

        foreach (var name in d.Headers.Keys)
        {
            if (RefusedHeaders.Contains(name) || string.IsNullOrWhiteSpace(name))
            {
                Add("error.customSource.headerRefused", new { header = name });
            }
        }

        foreach (var host in d.CoverHosts)
        {
            if (!HostPattern().IsMatch(host))
            {
                Add("error.customSource.coverHostInvalid", new { host });
            }
        }

        if (d.Search is not null && d.Catalogue is not null)
        {
            Add("error.customSource.searchAndCatalogue");
        }

        if (d.Engine is not null)
        {
            Engine(d, baseUri, Add);
        }
        else if (d.EngineOptions is not null)
        {
            Add("error.customSource.fieldInvalid", new { field = "engineOptions" });
        }

        if (baseUri is not null && d.Engine is not null)
        {
            Sample(baseUri, d, Add);
        }
        else if (baseUri is not null)
        {
            if (d.Search is { } search)
            {
                Template(baseUri, search.Url, "search.url", [CustomTemplate.Query], Add);
                List(search.Item, "search.item", Add);
                Fields(search.Item, Add, ("search.id", search.Id), ("search.title", search.Title), ("search.cover", search.Cover));
            }

            if (d.Catalogue is { } catalogue)
            {
                Template(baseUri, catalogue.Url, "catalogue.url", [], Add);
                List(catalogue.Item, "catalogue.item", Add);
                Fields(catalogue.Item, Add, ("catalogue.id", catalogue.Id), ("catalogue.title", catalogue.Title), ("catalogue.cover", catalogue.Cover));
                PageFields(Add, ("catalogue.nextPage", catalogue.NextPage));
            }

            Template(baseUri, d.Series.Url, "series.url", [CustomTemplate.SeriesId], Add);
            PageFields(Add, ("series.title", d.Series.Title), ("series.cover", d.Series.Cover),
                ("series.description", d.Series.Description), ("series.status", d.Series.Status));

            if (d.Chapters.ListUrl is { } listUrl)
            {
                Template(baseUri, listUrl, "chapters.listUrl", [CustomTemplate.SeriesId], Add);
            }

            Template(baseUri, d.Chapters.Url, "chapters.url", [CustomTemplate.ChapterId], Add);
            List(d.Chapters.Item, "chapters.item", Add);
            Fields(d.Chapters.Item, Add, ("chapters.id", d.Chapters.Id), ("chapters.number", d.Chapters.Number),
                ("chapters.title", d.Chapters.Title), ("chapters.volume", d.Chapters.Volume), ("chapters.date", d.Chapters.Date));
            PageFields(Add, ("chapters.nextPage", d.Chapters.NextPage));
            if (d.Chapters.Date is { Format: not ("relative" or "iso") and { Length: 0 } })
            {
                Add("error.customSource.fieldInvalid", new { field = "chapters.date.format" });
            }

            if (d.Pages.Url is { } pagesUrl)
            {
                Template(baseUri, pagesUrl, "pages.url", [], Add);
            }

            List(d.Pages.List, "pages.list", Add);
            Fields(d.Pages.List, Add, ("pages.image", d.Pages.Image));

            Sample(baseUri, d, Add);
        }

        return problems;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,40}$")]
    private static partial Regex PathSegmentPattern();

    /// <summary>An engine replaces the four sections, so a definition carrying both is ambiguous.</summary>
    private static void Engine(CustomSourceDefinition d, Uri? baseUri, Action<string, object?> add)
    {
        if (!CustomEngines.All.Contains(d.Engine))
        {
            add("error.customSource.fieldInvalid", new { field = "engine" });
            return;
        }

        if (d.Search is not null || d.Catalogue is not null || d.Series != new CustomSeries()
            || d.Chapters != new CustomChapters() || d.Pages != new CustomPages())
        {
            add("error.customSource.engineSections", new { engine = d.Engine });
        }

        var options = d.EngineOptions ?? new CustomEngineOptions();
        if (options.SeriesPath is { } seriesPath && !PathSegmentPattern().IsMatch(seriesPath))
        {
            add("error.customSource.fieldInvalid", new { field = "engineOptions.seriesPath" });
        }

        // The API is the one other host a definition may read, so it has to be the site's own.
        if (options.ApiUrl is not null && d.Engine != CustomEngines.HeanCms
            || d.Engine == CustomEngines.HeanCms && baseUri is not null && CustomSource.HeanApi(options, baseUri) is null)
        {
            add("error.customSource.fieldInvalid", new { field = "engineOptions.apiUrl" });
        }

        if (options.ChapterList is not (CustomEngineOptions.Inline or CustomEngineOptions.Ajax))
        {
            add("error.customSource.fieldInvalid", new { field = "engineOptions.chapterList" });
        }
    }

    private static void Sample(Uri baseUri, CustomSourceDefinition d, Action<string, object?> add)
    {
        SampleUrl(baseUri, d.Sample.SeriesUrl, "sample.seriesUrl", add);
        SampleUrl(baseUri, d.Sample.ChapterUrl, "sample.chapterUrl", add);
        if ((d.Search is not null || d.Engine is not null) && string.IsNullOrWhiteSpace(d.Sample.Query))
        {
            add("error.customSource.sampleQueryRequired", null);
        }
    }

    /// <summary>
    /// An http(s) origin with no path, query or credentials, on a host name: an IP literal or
    /// <c>localhost</c> is refused here for a readable error, while the connect-time guard on the
    /// client is what actually keeps fetches off the LAN.
    /// </summary>
    public static Uri? BaseUri(string? baseUrl)
    {
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath.Trim('/').Length > 0
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0
            || IPAddress.TryParse(uri.Host.Trim('[', ']'), out _)
            || uri.IsLoopback
            || !HostPattern().IsMatch(uri.IdnHost)
            || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
    }

    private static void Template(Uri baseUri, string template, string field, string[] required, Action<string, object?> add)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            add("error.customSource.fieldRequired", new { field });
            return;
        }

        foreach (var placeholder in required)
        {
            if (!CustomTemplate.Mentions(template, placeholder))
            {
                add("error.customSource.templatePlaceholder", new { field, placeholder });
            }
        }

        if (!Uri.TryCreate(baseUri, template.Replace("{", "").Replace("}", ""), out var resolved)
            || !CustomHost.Same(resolved.Host, baseUri.Host)
            || resolved.Scheme is not ("http" or "https"))
        {
            add("error.customSource.templateHost", new { field, host = baseUri.Host });
        }
    }

    private static void SampleUrl(Uri baseUri, string url, string field, Action<string, object?> add)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !CustomHost.Same(uri.Host, baseUri.Host))
        {
            add("error.customSource.templateHost", new { field, host = baseUri.Host });
        }
    }

    private static void List(ListExtractor list, string field, Action<string, object?> add)
    {
        var set = (list.Css is not null ? 1 : 0) + (list.Regex is not null ? 1 : 0) + (list.Json is not null ? 1 : 0);
        if (set != 1)
        {
            add("error.customSource.listExtractorShape", new { field });
            return;
        }

        Css(list.Css, field, add);
        Pattern(list.Regex, field, add);
        if (list.Json is { } json)
        {
            Css(json.Css, field, add);
            Pattern(json.Regex, field, add);
            Path(json.Path, field, add, required: true);
        }
    }

    /// <summary>A field may only read what its list's items carry: css on elements, a JSON path on JSON values.</summary>
    private static void Fields(ListExtractor list, Action<string, object?> add, params (string Field, FieldExtractor? Extractor)[] fields)
    {
        foreach (var (field, extractor) in fields)
        {
            if (extractor is null)
            {
                continue;
            }

            if (extractor.Value is not null)
            {
                if (extractor.Css is not null || extractor.Attr is not null || extractor.Regex is not null || extractor.Json is not null)
                {
                    add("error.customSource.fieldExtractorShape", new { field });
                }

                continue;
            }

            if ((extractor.Css is not null || extractor.Attr is not null) && list.Css is null
                || extractor.Json is not null && list.Json is null)
            {
                add("error.customSource.fieldExtractorShape", new { field });
            }

            Css(extractor.Css, field, add);
            Pattern(extractor.Regex, field, add);
            Path(extractor.Json, field, add, required: false);
        }
    }

    /// <summary>Fields read from the whole page: css, or a regex over the body.</summary>
    private static void PageFields(Action<string, object?> add, params (string Field, FieldExtractor? Extractor)[] fields)
    {
        foreach (var (field, extractor) in fields)
        {
            if (extractor is null)
            {
                continue;
            }

            if (extractor.Json is not null || extractor.Value is null && extractor.Css is null && extractor.Regex is null)
            {
                add("error.customSource.fieldExtractorShape", new { field });
            }

            Css(extractor.Css, field, add);
            Pattern(extractor.Regex, field, add);
        }
    }

    private static void Css(string? selector, string field, Action<string, object?> add)
    {
        if (selector is null)
        {
            return;
        }

        try
        {
            Empty.QuerySelectorAll(selector);
        }
        catch (DomException)
        {
            add("error.customSource.cssInvalid", new { field, selector });
        }
    }

    private static void Pattern(string? pattern, string field, Action<string, object?> add)
    {
        if (pattern is null)
        {
            return;
        }

        try
        {
            CustomExtractor.Compile(pattern);
        }
        catch (ArgumentException)
        {
            add("error.customSource.regexInvalid", new { field, pattern });
        }
    }

    private static void Path(string? path, string field, Action<string, object?> add, bool required)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            if (required)
            {
                add("error.customSource.fieldRequired", new { field = field + ".json.path" });
            }

            return;
        }

        try
        {
            CustomExtractor.Segments(path);
        }
        catch (FormatException)
        {
            add("error.customSource.jsonPathInvalid", new { field, path });
        }
    }
}
