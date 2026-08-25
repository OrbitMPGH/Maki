using System.Text.Json;
using Maki.Core.Entities;

namespace Maki.Core.Configuration;

/// <summary>
/// Maps a series' provider content rating onto the <see cref="IncognitoMode"/> a newly added series
/// starts at, stored as a JSON object under <see cref="SettingKeys.LibraryIncognitoByRating"/>
/// (<c>{"pornographic":"Full"}</c>).
/// <para>
/// The rule is applied at <em>add</em> time only, never on refresh: it decides a default, and once a
/// series is in the library its incognito setting belongs to whoever set it. A metadata refresh that
/// re-rates a title would otherwise silently undo a manual change.
/// </para>
/// </summary>
public static class IncognitoRatingRules
{
    /// <summary>
    /// Applied when the setting has never been written. Pornographic titles start fully incognito:
    /// the alternative is that adding one pushes it to public tracker profiles before anyone has
    /// had a chance to say otherwise, which is not a default anybody wants to discover afterwards.
    /// Everything else defaults to off, so an upgrade doesn't quietly hide the existing library's
    /// worth of adds.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IncognitoMode> Default =
        new Dictionary<string, IncognitoMode>(StringComparer.OrdinalIgnoreCase)
        {
            ["pornographic"] = IncognitoMode.Full,
        };

    /// <summary>
    /// Reads the stored blob. Null/blank/unparseable falls back to <see cref="Default"/> — an empty
    /// object <c>{}</c> does not, since "every rating set to off" is a choice somebody made and has
    /// to survive a round trip.
    /// </summary>
    public static IReadOnlyDictionary<string, IncognitoMode> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Default;
        }

        Dictionary<string, string>? raw;
        try
        {
            raw = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch (JsonException)
        {
            return Default;
        }

        if (raw is null)
        {
            return Default;
        }

        var parsed = new Dictionary<string, IncognitoMode>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rating, mode) in raw)
        {
            if (Enum.TryParse<IncognitoMode>(mode, true, out var parsedMode))
            {
                parsed[rating] = parsedMode;
            }
        }

        return parsed;
    }

    public static string Serialize(IReadOnlyDictionary<string, IncognitoMode> rules) =>
        JsonSerializer.Serialize(rules.ToDictionary(r => r.Key, r => r.Value.ToString()));

    /// <summary>
    /// The mode a series with <paramref name="contentRating"/> should start at. An unknown or absent
    /// rating (a series whose metadata predates the column) matches no rule and stays off.
    /// </summary>
    public static IncognitoMode Resolve(IReadOnlyDictionary<string, IncognitoMode> rules, string? contentRating) =>
        contentRating is not null && rules.TryGetValue(contentRating, out var mode)
            ? mode
            : IncognitoMode.Off;
}
