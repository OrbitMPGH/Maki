using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Maki.Core.Sources;
using Maki.Core.Sources.Custom;
using Maki.Sources.Common;
using Maki.Sources.Toonily;

namespace Maki.Sources.Custom;

/// <summary>One fetched response, parsed as HTML only when an extractor asks for it.</summary>
public sealed class CustomPage(string body, Uri url)
{
    private static readonly HtmlParser Parser = new();
    private IDocument? _document;

    public string Body { get; } = body;
    public Uri Url { get; } = url;
    public IDocument Document => _document ??= Parser.ParseDocument(Body);
}

/// <summary>One list item: an element, a matched string, or a JSON value.</summary>
public readonly record struct CustomItem(IElement? Element, string? Text, JsonElement? Json)
{
    /// <summary>The item's own link, used as the chapter-number fallback and the round-trip check.</summary>
    public string? Href => Element?.GetAttribute("href") ?? Element?.QuerySelector("a[href]")?.GetAttribute("href");
}

/// <summary>Runs <see cref="ListExtractor"/>s and <see cref="FieldExtractor"/>s. Every regex has a match timeout.</summary>
public static class CustomExtractor
{
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly ConcurrentDictionary<string, Regex> Compiled = new();

    public static Regex Compile(string pattern) => Compiled.GetOrAdd(pattern, p =>
    {
        try
        {
            return new Regex(p, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (NotSupportedException)
        {
            // Lookarounds and backreferences need the backtracking engine; the timeout still bounds it.
            return new Regex(p, RegexOptions.CultureInvariant, RegexTimeout);
        }
    });

    public static IReadOnlyList<CustomItem> List(ListExtractor list, CustomPage page)
    {
        if (list.Css is { } css)
        {
            return page.Document.QuerySelectorAll(css).Select(e => new CustomItem(e, null, null)).ToList();
        }

        if (list.Regex is { } pattern)
        {
            var regex = Compile(pattern);
            return regex.Matches(page.Body)
                .Select(m => new CustomItem(null, Unescape(m.Groups.Count > 1 ? m.Groups[1].Value : m.Value), null))
                .ToList();
        }

        if (list.Json is { } json)
        {
            var blob = JsonBlob(json, page);
            if (blob is null)
            {
                return [];
            }

            using var doc = JsonDocument.Parse(blob);
            return Walk(doc.RootElement, json.Path).Select(e => new CustomItem(null, null, e.Clone())).ToList();
        }

        return [];
    }

    public static string? Field(FieldExtractor? field, CustomItem item)
    {
        if (field is null)
        {
            return null;
        }

        if (field.Value is not null)
        {
            return field.Value;
        }

        string? raw;
        if (item.Element is { } element)
        {
            var target = field.Css is { } css ? element.QuerySelector(css) : element;
            raw = target is null ? null : Read(target, field.Attr);
        }
        else if (item.Json is { } json)
        {
            raw = field.Json is { } path ? Walk(json, path).Select(Scalar).FirstOrDefault(v => v is not null) : Scalar(json);
        }
        else
        {
            raw = item.Text;
        }

        return Narrow(raw, field.Regex);
    }

    /// <summary>A field read from the whole page rather than one item (series title, next page link).</summary>
    public static string? PageField(FieldExtractor? field, CustomPage page)
    {
        if (field is null)
        {
            return null;
        }

        if (field.Value is not null)
        {
            return field.Value;
        }

        if (field.Css is { } css)
        {
            var element = page.Document.QuerySelector(css);
            return element is null ? null : Narrow(Read(element, field.Attr), field.Regex);
        }

        if (field.Regex is { } pattern)
        {
            var match = Compile(pattern).Match(page.Body);
            return match.Success ? Unescape(match.Groups.Count > 1 ? match.Groups[1].Value : match.Value) : null;
        }

        return null;
    }

    /// <summary>The URL an image item names, when no <see cref="CustomPages.Image"/> field says otherwise.</summary>
    public static string? DefaultImage(CustomItem item)
    {
        if (item.Element is { } element)
        {
            var img = element.LocalName == "img" ? element : element.QuerySelector("img");
            return MadaraParser.ImageUrl(img);
        }

        return item.Json is { } json ? Scalar(json) : item.Text;
    }

    private static string? Read(IElement element, string? attr)
    {
        if (attr is null)
        {
            var text = element.TextContent;
            return string.IsNullOrWhiteSpace(text) ? null : CollapseWhitespace(text);
        }

        if (attr == "src" && element.LocalName == "img")
        {
            return MadaraParser.ImageUrl(element);
        }

        return element.GetAttribute(attr)?.Trim();
    }

    public static string? Narrow(string? raw, string? pattern)
    {
        if (raw is null || pattern is null)
        {
            return string.IsNullOrWhiteSpace(raw) ? null : raw;
        }

        var match = Compile(pattern).Match(raw);
        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? JsonBlob(JsonExtractor json, CustomPage page)
    {
        if (json.Css is { } css)
        {
            var element = page.Document.QuerySelector(css);
            return element is null ? null : json.Attr is { } attr ? element.GetAttribute(attr) : element.TextContent;
        }

        if (json.Regex is { } pattern)
        {
            var match = Compile(pattern).Match(page.Body);
            return match.Success ? (match.Groups.Count > 1 ? match.Groups[1].Value : match.Value) : null;
        }

        return PreUnwrap.Unwrap(page.Body, page.Url.ToString());
    }

    /// <summary>Dot segments, <c>name[*]</c> or <c>[*]</c> for every element, <c>name[n]</c> for one.</summary>
    public static IEnumerable<JsonElement> Walk(JsonElement root, string path)
    {
        IEnumerable<JsonElement> current = [root];
        foreach (var segment in Segments(path))
        {
            current = current.SelectMany(e => Step(e, segment)).ToList();
        }

        return current;
    }

    public readonly record struct PathSegment(string? Name, int? Index, bool All);

    public static IReadOnlyList<PathSegment> Segments(string path)
    {
        var segments = new List<PathSegment>();
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bracket = part.IndexOf('[');
            var name = bracket < 0 ? part : part[..bracket];
            if (name.Length > 0)
            {
                segments.Add(new PathSegment(name, null, false));
            }

            var rest = bracket < 0 ? "" : part[bracket..];
            while (rest.Length > 0)
            {
                var close = rest.IndexOf(']');
                if (!rest.StartsWith('[') || close < 0)
                {
                    throw new FormatException($"Bad JSON path segment '{part}'");
                }

                var inner = rest[1..close];
                segments.Add(inner == "*"
                    ? new PathSegment(null, null, true)
                    : int.TryParse(inner, out var index) && index >= 0
                        ? new PathSegment(null, index, false)
                        : throw new FormatException($"Bad JSON path index '{inner}'"));
                rest = rest[(close + 1)..];
            }
        }

        return segments;
    }

    private static IEnumerable<JsonElement> Step(JsonElement element, PathSegment segment)
    {
        if (segment.Name is { } name)
        {
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var child))
            {
                yield return child;
            }

            yield break;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        if (segment.All)
        {
            foreach (var item in element.EnumerateArray())
            {
                yield return item;
            }
        }
        else if (segment.Index is { } index && index < element.GetArrayLength())
        {
            yield return element[index];
        }
    }

    private static string? Scalar(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null
    };

    /// <summary>Script payloads escape slashes and HTML escapes ampersands; a matched URL needs neither.</summary>
    private static string Unescape(string value) =>
        WebUtility.HtmlDecode(value.Replace("\\/", "/", StringComparison.Ordinal).Replace("\\u0026", "&", StringComparison.OrdinalIgnoreCase));

    private static string CollapseWhitespace(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
