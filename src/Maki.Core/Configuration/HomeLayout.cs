using System.Text.Json;

namespace Maki.Core.Configuration;

/// <summary>
/// One row of the Home dashboard, as the user arranged it.
/// </summary>
/// <param name="Key">One of <see cref="HomeSections"/>' values.</param>
/// <param name="Enabled">False hides the section without forgetting where it sat.</param>
public record HomeSection(string Key, bool Enabled = true);

/// <summary>The canonical section list: every key Home knows, in the order it ships in.</summary>
public static class HomeSections
{
    public const string ContinueReading = "continue";
    public const string Downloading = "downloading";
    public const string RecentlyAdded = "recent";
    public const string JumpBackIn = "jumpback";
    public const string Recommended = "recommended";
    public const string Popular = "popular";
    public const string Stats = "stats";

    /// <summary>
    /// The reader's own progression: level, streak, goals and the latest badges. Distinct from
    /// <see cref="Stats"/>, which counts what is in the library.
    /// </summary>
    public const string Progress = "progress";

    /// <summary>
    /// Default order. Adding a key here is the only supported way to introduce a section — see
    /// <see cref="HomeLayoutSpec.Merge"/> for what existing users' stored layouts do with it.
    /// </summary>
    public static readonly string[] All =
    [
        Stats, Progress, Downloading, ContinueReading, JumpBackIn, RecentlyAdded, Recommended, Popular
    ];

    public static bool IsValid(string? key) => key is not null && All.Contains(key);
}

/// <summary>
/// Which Home sections are shown, and in what order — or whether Home exists at all.
/// <para>
/// Same discipline as <see cref="Maki.Core.Reading.ReaderPrefsSpec"/> and <c>SavedFilter.Spec</c>:
/// serialize only through <see cref="Json"/>, and never rename or reorder a property. A name
/// mismatch does not throw, it silently yields the parameter default, so a renamed field degrades
/// into "the user's layout was forgotten" rather than an error.
/// </para>
/// <para>
/// <see cref="Enabled"/> false turns Home off entirely for people who don't read in Maki: the nav
/// drops the tab, <c>/home</c> redirects to the library, and "/" can no longer resolve there.
/// </para>
/// </summary>
public record HomeLayoutSpec(bool Enabled = true, IReadOnlyList<HomeSection>? Sections = null)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Every known section, in shipping order, all on.</summary>
    public static HomeLayoutSpec Default => new(true, [.. HomeSections.All.Select(k => new HomeSection(k))]);

    /// <summary>
    /// Reconciles a stored layout with the canonical section list, which is what makes the setting
    /// survive a release that adds or removes a section:
    /// <list type="bullet">
    /// <item>keys this build no longer knows are dropped, so a downgrade can't render a ghost row;</item>
    /// <item>keys the stored layout has never seen are <b>appended, enabled</b> — appended rather
    /// than slotted into their canonical position because the user's own ordering is the thing
    /// worth preserving, and a new section they haven't seen has no business jumping above it;</item>
    /// <item>duplicates collapse to their first occurrence.</item>
    /// </list>
    /// </summary>
    public HomeLayoutSpec Merge()
    {
        var seen = new HashSet<string>();
        var ordered = new List<HomeSection>(HomeSections.All.Length);

        foreach (var section in Sections ?? [])
        {
            if (HomeSections.IsValid(section.Key) && seen.Add(section.Key))
            {
                ordered.Add(section);
            }
        }

        foreach (var key in HomeSections.All)
        {
            if (seen.Add(key))
            {
                ordered.Add(new HomeSection(key));
            }
        }

        return this with { Sections = ordered };
    }

    /// <summary>Reads a stored blob, falling back to the default layout for null/blank/bad JSON.</summary>
    public static HomeLayoutSpec Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Default;
        }

        try
        {
            return (JsonSerializer.Deserialize<HomeLayoutSpec>(json, Json) ?? Default).Merge();
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    public static string Serialize(HomeLayoutSpec spec) => JsonSerializer.Serialize(spec.Merge(), Json);
}
