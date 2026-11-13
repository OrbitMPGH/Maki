using System.Text.Json;
using Maki.Core.Entities;

namespace Maki.Core.Tags;

/// <summary>One test a series must pass. Field names and values persist in <see cref="AutoTagRule.ConditionsJson"/>.</summary>
public sealed record AutoTagCondition(string Field, string Value);

/// <summary>Persisted condition field names. Append only.</summary>
public static class AutoTagFields
{
    public const string Genre = "genre";
    public const string Type = "type";
    public const string ContentRating = "contentRating";
    public const string AddedVia = "addedVia";
    public const string AddedBy = "addedBy";
    public const string RootFolder = "rootFolder";

    public static readonly string[] All = [Genre, Type, ContentRating, AddedVia, AddedBy, RootFolder];

    /// <summary>What a root folder rule may test: everything but the root folder, which it decides.</summary>
    public static readonly string[] ForRootFolderRules = [Genre, Type, ContentRating, AddedVia, AddedBy];
}

/// <summary>How a series came into the library, as a rule names it. Persisted; append only.</summary>
public static class AutoTagAddedVia
{
    /// <summary>The add form or a search result: anything not covered below.</summary>
    public const string Manual = "manual";
    public const string Discover = "discover";
    public const string Request = "request";
    public const string ImportList = "importList";
    public const string Mihon = "mihon";
    public const string LibraryImport = "libraryImport";

    public static readonly string[] All = [Manual, Discover, Request, ImportList, Mihon, LibraryImport];

    /// <summary>
    /// The rule vocabulary for a <c>UserSeriesState.AddedFrom</c> value, which the add path stores as
    /// <c>recommendation</c>, <c>request</c>, <c>importlist</c>, <c>mihon</c> or <c>library</c>.
    /// </summary>
    public static string FromAddedFrom(string? addedFrom) => addedFrom switch
    {
        "recommendation" => Discover,
        "request" => Request,
        "importlist" => ImportList,
        "mihon" => Mihon,
        _ => Manual,
    };
}

/// <summary>What a rule is tested against: the series' own fields plus how and by whom it was added.</summary>
public sealed record AutoTagSubject(
    IReadOnlyCollection<string> Genres,
    string? Type,
    string? ContentRating,
    int RootFolderId,
    string? AddedVia,
    int? AddedBy)
{
    public static AutoTagSubject For(Series series, string? addedVia, int? addedBy) =>
        new(series.Genres, series.Type, series.ContentRating, series.RootFolderId, addedVia, addedBy);
}

public static class AutoTagRules
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>MangaBaka's content ratings, as <c>Series.ContentRating</c> stores them.</summary>
    private static readonly string[] ContentRatings = ["safe", "suggestive", "erotica", "pornographic"];

    /// <summary>The stored conditions; an unreadable list reads as empty, which matches nothing.</summary>
    public static IReadOnlyList<AutoTagCondition> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<AutoTagCondition>>(json, Json)?
                .Where(c => c is { Field: not null, Value: not null })
                .ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string Serialize(IEnumerable<AutoTagCondition> conditions) =>
        JsonSerializer.Serialize(conditions.ToList(), Json);

    /// <summary>
    /// Trims and validates a submitted list. Null when it is empty (a rule with no condition would tag
    /// every series) or names a field or value outside the closed sets.
    /// </summary>
    public static IReadOnlyList<AutoTagCondition>? Normalize(IEnumerable<AutoTagCondition>? conditions) =>
        Normalize(conditions, AutoTagFields.All);

    /// <summary>The same, accepting only <paramref name="fields"/>.</summary>
    public static IReadOnlyList<AutoTagCondition>? Normalize(
        IEnumerable<AutoTagCondition>? conditions, IReadOnlyCollection<string> fields)
    {
        var result = new List<AutoTagCondition>();
        foreach (var c in conditions ?? [])
        {
            if (NormalizeOne(c, fields) is not { } normalized)
            {
                return null;
            }

            result.Add(normalized);
        }

        return result.Count == 0 || result.Count > MaxConditions ? null : result.Distinct().ToList();
    }

    /// <summary>The most conditions one rule may hold, across all its groups.</summary>
    public const int MaxConditions = 20;

    internal static AutoTagCondition? NormalizeOne(AutoTagCondition c, IReadOnlyCollection<string> fields)
    {
        var field = c.Field?.Trim() ?? "";
        var value = c.Value?.Trim() ?? "";
        if (!fields.Contains(field) || value.Length == 0)
        {
            return null;
        }

        var valid = field switch
        {
            AutoTagFields.Type => SeriesTypes.All.Contains(value),
            AutoTagFields.AddedVia => AutoTagAddedVia.All.Contains(value),
            AutoTagFields.ContentRating => ContentRatings.Contains(value),
            AutoTagFields.AddedBy or AutoTagFields.RootFolder => int.TryParse(value, out var id) && id > 0,
            _ => value.Length <= 100,
        };
        return valid ? new AutoTagCondition(field, value) : null;
    }

    /// <summary>Every condition must hold. An empty list matches nothing.</summary>
    public static bool Matches(IReadOnlyList<AutoTagCondition> conditions, AutoTagSubject subject) =>
        conditions.Count > 0 && conditions.All(c => Holds(c, subject));

    internal static bool Holds(AutoTagCondition c, AutoTagSubject s) => c.Field switch
    {
        AutoTagFields.Genre => s.Genres.Any(g => string.Equals(g.Trim(), c.Value, StringComparison.OrdinalIgnoreCase)),
        AutoTagFields.Type => string.Equals(s.Type, c.Value, StringComparison.OrdinalIgnoreCase),
        AutoTagFields.ContentRating => string.Equals(s.ContentRating, c.Value, StringComparison.OrdinalIgnoreCase),
        AutoTagFields.AddedVia => s.AddedVia == c.Value,
        AutoTagFields.AddedBy => s.AddedBy is { } user && c.Value == user.ToString(System.Globalization.CultureInfo.InvariantCulture),
        AutoTagFields.RootFolder => c.Value == s.RootFolderId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => false,
    };
}
