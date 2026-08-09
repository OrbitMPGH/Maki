using System.Text.Json;

namespace Maki.Core.Configuration;

/// <summary>
/// One user's progression preferences: whether the achievement, level and streak surfaces exist for
/// them at all, and whether they are willing to be compared against the other people on the instance.
/// <para>
/// This is a display preference, never a store of progress. Everything shown is recomputed from
/// <c>StatsEvents</c> on demand, so turning the feature off and back on loses nothing — which is what
/// lets the master switch be a genuine "hide all of this" for the self-hosting persona rather than a
/// decision they have to make before any reading is recorded.
/// </para>
/// <para>
/// Same discipline as <see cref="HomeLayoutSpec"/> and <see cref="RecommendationDefaultsSpec"/>:
/// serialize only through <see cref="Json"/>, and never rename or reorder a property. A name mismatch
/// does not throw, it silently yields the parameter default, so a rename degrades into "the user's
/// preference was forgotten" rather than an error.
/// </para>
/// </summary>
/// <param name="Enabled">
/// False hides every surface: the Home section, the Stats page's all-time tab, and the reader's
/// unlock toast. The API stops evaluating too, so nothing is written while it is off.
/// </param>
/// <param name="ShowStreaks">
/// Streaks are the one mechanic here with any capacity to nag, so they get their own switch. Off
/// keeps achievements and levels while dropping the streak counter and its two achievements from
/// display; the underlying numbers are still computed, since the streak achievements stay earnable.
/// </param>
/// <param name="ShowOnLeaderboard">
/// Opt <em>in</em> to being listed for the other users of this instance. Off by default: reading is
/// per-user here and the shared thing is the library, so appearing in someone else's view is a
/// choice. Never inferred from anything else.
/// </param>
public record ProgressSpec(
    bool Enabled = true,
    bool ShowStreaks = true,
    bool ShowOnLeaderboard = false)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>What an unset preference reads back as: on, streaks shown, not shared.</summary>
    public static readonly ProgressSpec Default = new();

    /// <summary>Reads a stored blob; null/blank/unreadable JSON reads as <see cref="Default"/>.</summary>
    public static ProgressSpec Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Default;
        }

        try
        {
            return JsonSerializer.Deserialize<ProgressSpec>(json, Json) ?? Default;
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    public static string Serialize(ProgressSpec spec) => JsonSerializer.Serialize(spec, Json);
}
