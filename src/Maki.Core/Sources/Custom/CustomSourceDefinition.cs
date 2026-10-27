using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maki.Core.Sources.Custom;

/// <summary>
/// A site an admin added without a Maki release: URL templates plus extractors that one generic
/// engine runs (<c>Maki.Sources.Custom.CustomSource</c>). Data only, never code. Stored as a JSON
/// array under <see cref="Configuration.SettingKeys.CustomSources"/>; the format is documented in
/// <c>docs/custom-sources-plan.md</c>.
/// </summary>
public sealed record CustomSourceDefinition
{
    public const int CurrentSchema = 1;

    /// <summary>Prefix of every custom source's <see cref="ISource.Name"/>, so none can take a built-in's or Suwayomi's.</summary>
    public const string NamePrefix = "custom-";

    public int Schema { get; init; } = CurrentSchema;
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string BaseUrl { get; init; } = "";
    public string Language { get; init; } = "en";
    public string Kind { get; init; } = "scanlator";
    public List<string> Content { get; init; } = ["manga"];
    public string Rating { get; init; } = "general";
    public bool FlareSolverr { get; init; }
    public int MinIntervalMs { get; init; } = DefaultIntervalMs;
    public Dictionary<string, string> Headers { get; init; } = [];
    public List<string> CoverHosts { get; init; } = [];

    /// <summary>
    /// A built-in parser for a known site theme (<see cref="CustomEngines"/>) in place of the
    /// search, series, chapters and pages sections, which are then left out.
    /// </summary>
    public string? Engine { get; init; }
    public CustomEngineOptions? EngineOptions { get; init; }

    public CustomSearch? Search { get; init; }
    public CustomCatalogue? Catalogue { get; init; }
    public CustomSeries Series { get; init; } = new();
    public CustomChapters Chapters { get; init; } = new();
    public CustomPages Pages { get; init; } = new();
    public CustomSample Sample { get; init; } = new();
    public CustomOrigin? Origin { get; init; }

    public const int DefaultIntervalMs = 1000;

    [JsonIgnore]
    public string SourceName => NamePrefix + Id;

    [JsonIgnore]
    public bool HasSearch => Engine is not null || Search is not null || Catalogue is not null;

    /// <summary>camelCase, unknown members refused, so a typo in a field name fails instead of silently doing nothing.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static CustomSourceDefinition Parse(string json) =>
        JsonSerializer.Deserialize<CustomSourceDefinition>(json, Json)
        ?? throw new JsonException("A custom source definition cannot be null");

    public static CustomSourceDefinition Parse(JsonElement json) =>
        json.Deserialize<CustomSourceDefinition>(Json)
        ?? throw new JsonException("A custom source definition cannot be null");

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}

public static class CustomEngines
{
    /// <summary>The Madara WordPress theme most small scanlators run.</summary>
    public const string Madara = "madara";

    public static readonly IReadOnlyList<string> All = [Madara];
}

public sealed record CustomEngineOptions
{
    /// <summary>The path segment series live under: <c>manga</c> on most Madara sites, <c>serie</c> on Toonily.</summary>
    public string SeriesPath { get; init; } = "manga";

    /// <summary><c>inline</c> when the series page renders the list, <c>ajax</c> when the theme loads it with a POST.</summary>
    public string ChapterList { get; init; } = Inline;

    public const string Inline = "inline";
    public const string Ajax = "ajax";
}

public sealed record CustomSearch
{
    /// <summary>Template with <c>{query}</c>.</summary>
    public string Url { get; init; } = "";
    public ListExtractor Item { get; init; } = new();
    public FieldExtractor Id { get; init; } = new();
    public FieldExtractor? Title { get; init; }
    public FieldExtractor? Cover { get; init; }
}

/// <summary>A list page read in full and matched by title, for a site with no search.</summary>
public sealed record CustomCatalogue
{
    public string Url { get; init; } = "";
    public ListExtractor Item { get; init; } = new();
    public FieldExtractor Id { get; init; } = new();
    public FieldExtractor? Title { get; init; }
    public FieldExtractor? Cover { get; init; }
    public FieldExtractor? NextPage { get; init; }
}

public sealed record CustomSeries
{
    /// <summary>Template with <c>{seriesId}</c>. Inverted for <see cref="ISource.ResolveSeriesIdFromUrl"/>.</summary>
    public string Url { get; init; } = "";
    public FieldExtractor? Title { get; init; }
    public FieldExtractor? Cover { get; init; }
    public FieldExtractor? Description { get; init; }
    public FieldExtractor? Status { get; init; }
}

public sealed record CustomChapters
{
    /// <summary>Defaults to the series page.</summary>
    public string? ListUrl { get; init; }
    public ListExtractor Item { get; init; } = new();
    public FieldExtractor Id { get; init; } = new();

    /// <summary>
    /// Required template over <c>{seriesId}</c> and <c>{chapterId}</c>. The download path rebuilds a
    /// chapter from its stored ids alone, without the URL it was listed with.
    /// </summary>
    public string Url { get; init; } = "";
    public FieldExtractor? Number { get; init; }
    public FieldExtractor? Title { get; init; }
    public FieldExtractor? Volume { get; init; }
    public DateExtractor? Date { get; init; }
    public FieldExtractor? NextPage { get; init; }
}

public sealed record CustomPages
{
    /// <summary>Defaults to the chapter's own URL.</summary>
    public string? Url { get; init; }
    public ListExtractor List { get; init; } = new();

    /// <summary>The image URL of one list item. Defaults to an <c>img</c>'s lazy-load attributes, srcset or src, or the item's own text.</summary>
    public FieldExtractor? Image { get; init; }
}

public sealed record CustomSample
{
    public string SeriesUrl { get; init; } = "";
    public string ChapterUrl { get; init; } = "";
    public string? Query { get; init; }
}

public sealed record CustomOrigin
{
    public string Repository { get; init; } = "";
    public int Version { get; init; }
    public string? Sha256 { get; init; }
}

/// <summary>Exactly one of <see cref="Css"/>, <see cref="Regex"/> or <see cref="Json"/>.</summary>
public sealed record ListExtractor
{
    public string? Css { get; init; }

    /// <summary>Every match over the raw response; group 1 when the pattern has one.</summary>
    public string? Regex { get; init; }
    public JsonExtractor? Json { get; init; }
}

/// <summary>
/// A JSON blob, then a path of dot segments with <c>[*]</c> for "every element" and <c>[n]</c> for one.
/// The blob is the response body, or the text (or <see cref="Attr"/>) of the first element matching
/// <see cref="Css"/>, or group 1 of the first match of <see cref="Regex"/>.
/// </summary>
public sealed record JsonExtractor
{
    public string Path { get; init; } = "";
    public string? Css { get; init; }
    public string? Attr { get; init; }
    public string? Regex { get; init; }
}

/// <summary>
/// One value out of one list item: a descendant (<see cref="Css"/>) or the item itself, then an
/// attribute or its text, then <see cref="Regex"/> group 1. On a JSON item <see cref="Json"/> is a
/// path relative to it. <see cref="Value"/> is a literal and excludes the rest.
/// </summary>
public record FieldExtractor
{
    public string? Css { get; init; }
    public string? Attr { get; init; }
    public string? Regex { get; init; }
    public string? Json { get; init; }
    public string? Value { get; init; }
}

public sealed record DateExtractor : FieldExtractor
{
    /// <summary><c>relative</c>, <c>iso</c>, or a .NET format string read with <see cref="Culture"/>.</summary>
    public string Format { get; init; } = "relative";
    public string? Culture { get; init; }
}
