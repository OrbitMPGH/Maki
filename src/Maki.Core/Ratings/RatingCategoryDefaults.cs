using System.Text.Json;
using System.Text.RegularExpressions;

namespace Maki.Core.Ratings;

/// <summary>
/// One rating breakdown category. Built-ins carry a null <see cref="Name"/> (the client translates
/// them); a custom one (<c>c-</c> key) carries the name its owner typed.
/// </summary>
public sealed record RatingCategory(string Key, string? Name, bool Enabled, double Weight);

/// <summary>
/// The per-user list of rating breakdown categories stored under <c>ratings.categories</c>: the
/// built-ins, their defaults, and the rules a saved list has to follow. Array order is display order.
/// </summary>
public static partial class RatingCategoryDefaults
{
    public const int MaxEnabled = 8;
    public const int MaxCustom = 8;
    public const int MaxNameLength = 40;

    public static readonly IReadOnlyList<string> BuiltInKeys =
        ["story", "characters", "art", "enjoyment", "pacing", "world", "originality", "ending"];

    private const int DefaultEnabledCount = 4;
    private static readonly double[] Weights = [0.5, 1, 2];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<RatingCategory> Defaults { get; } =
        BuiltInKeys.Select((key, i) => new RatingCategory(key, null, i < DefaultEnabledCount, 1)).ToList();

    public static bool IsBuiltIn(string key) => BuiltInKeys.Contains(key);

    public static bool IsCustomKey(string key) => CustomKeyRegex().IsMatch(key);

    /// <summary>The stored list, with any missing built-in appended. Unset or unreadable means the defaults.</summary>
    public static IReadOnlyList<RatingCategory> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Defaults;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<List<RatingCategory>>(json, Json);
            return stored is not null && Normalize(stored) is { } valid ? valid : Defaults;
        }
        catch (JsonException)
        {
            return Defaults;
        }
    }

    public static string Serialize(IReadOnlyList<RatingCategory> categories) =>
        JsonSerializer.Serialize(categories, Json);

    /// <summary>
    /// Checks a list a user sent and returns it cleaned up (names trimmed, built-in names dropped,
    /// missing built-ins appended), or null when it breaks a rule.
    /// </summary>
    public static IReadOnlyList<RatingCategory>? Normalize(IReadOnlyList<RatingCategory?>? categories)
    {
        if (categories is null)
        {
            return null;
        }

        var result = new List<RatingCategory>(categories.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var category in categories)
        {
            if (category?.Key is not { } key || !seen.Add(key) || !Weights.Contains(category.Weight))
            {
                return null;
            }

            if (IsBuiltIn(key))
            {
                result.Add(category with { Name = null });
            }
            else if (IsCustomKey(key) && category.Name?.Trim() is { Length: > 0 and <= MaxNameLength } name)
            {
                result.Add(category with { Name = name });
            }
            else
            {
                return null;
            }
        }

        if (result.Count(c => !IsBuiltIn(c.Key)) > MaxCustom || result.Count(c => c.Enabled) > MaxEnabled)
        {
            return null;
        }

        foreach (var missing in Defaults.Where(d => !seen.Contains(d.Key)))
        {
            var room = result.Count(c => c.Enabled) < MaxEnabled;
            result.Add(missing with { Enabled = missing.Enabled && room });
        }

        return result;
    }

    [GeneratedRegex("^c-[a-z0-9]{1,37}$")]
    private static partial Regex CustomKeyRegex();
}
