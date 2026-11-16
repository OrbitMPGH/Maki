using System.Text.Json;
using System.Text.RegularExpressions;

namespace Maki.Core.Ratings;

/// <summary>
/// A reader's per-category scores on one series, stored as a JSON object of category key to a whole
/// 1-10 score (<c>{"story":8,"art":6}</c>). Keys are not checked against the reader's category
/// list, so turning a category off later keeps its score.
/// </summary>
public static partial class RatingBreakdown
{
    public const int MaxEntries = 16;

    /// <summary>
    /// Checks scores a user sent. Returns the scores in their order, an empty map for "clear", or
    /// null when a key or value breaks a rule.
    /// </summary>
    public static IReadOnlyDictionary<string, int>? Validate(IReadOnlyDictionary<string, JsonElement>? scores)
    {
        if (scores is null || scores.Count > MaxEntries)
        {
            return null;
        }

        var result = new Dictionary<string, int>(scores.Count, StringComparer.Ordinal);
        foreach (var (key, value) in scores)
        {
            if (!KeyRegex().IsMatch(key) || value.ValueKind != JsonValueKind.Number ||
                !value.TryGetInt32(out var score) || score is < 1 or > 10)
            {
                return null;
            }

            result[key] = score;
        }

        return result;
    }

    /// <summary>The stored value for <paramref name="scores"/>: null when there is nothing to keep.</summary>
    public static string? Serialize(IReadOnlyDictionary<string, int> scores) =>
        scores.Count == 0 ? null : JsonSerializer.Serialize(scores);

    /// <summary>The stored breakdown, or null when unset, empty or unreadable.</summary>
    public static IReadOnlyDictionary<string, int>? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
            return parsed is { Count: > 0 } ? parsed : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex("^[a-z0-9-]{1,40}$")]
    private static partial Regex KeyRegex();
}
