using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maki.Core.Configuration;

/// <summary>One part of a section that shows several things side by side, switched on or off on its own.</summary>
public record PagePanel(string Key, bool Enabled = true);

/// <summary>
/// One section of a user-arranged page (Home, Discover), as the user arranged it.
/// <para>
/// Serialized as part of a stored layout blob: never rename a property. A name mismatch does not
/// throw, it silently yields the parameter default.
/// </para>
/// </summary>
/// <param name="Key">A section key from the page's definition, or a custom rail's <c>rail:{id}</c>.</param>
/// <param name="Enabled">False hides the section without forgetting where it sat.</param>
/// <param name="Hero">
/// Whether the section leads with large tiles. Only meaningful on sections whose definition lists a
/// hero default; <see cref="PageLayouts.Merge"/> resolves it to a value there and to null elsewhere.
/// </param>
/// <param name="Panels">The parts of a multi-panel section, in the user's order. Null elsewhere.</param>
public record PageSection(
    string Key,
    bool Enabled = true,
    bool? Hero = null,
    IReadOnlyList<PagePanel>? Panels = null);

/// <summary>What one page knows about its sections, which is everything <see cref="PageLayouts.Merge"/> needs.</summary>
/// <param name="Keys">Every built-in section, in the order the page ships in.</param>
/// <param name="HeroDefaults">Sections that can lead with large tiles, and whether they do by default.</param>
/// <param name="Panels">Sections made of panels, and their panels in shipping order.</param>
/// <param name="RailAnchor">
/// Where a custom rail the layout has never seen goes: after the last rail already placed, or, with
/// none placed, right before this section. Null appends it at the end.
/// </param>
/// <param name="Upgrade">Rewrites a layout stored by an older build before anything else runs.</param>
public sealed record PageLayoutDefinition(
    IReadOnlyList<string> Keys,
    IReadOnlyDictionary<string, bool> HeroDefaults,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Panels,
    string? RailAnchor = null,
    Func<IReadOnlyList<PageSection>, IReadOnlyList<PageSection>>? Upgrade = null);

/// <summary>A custom rail's key in a page layout: <c>rail:{id}</c>.</summary>
public static class LayoutRails
{
    public const string Prefix = "rail:";

    public static string Key(int id) => Prefix + id.ToString(CultureInfo.InvariantCulture);

    public static bool TryParse(string? key, out int id)
    {
        id = 0;
        return key is not null &&
            key.StartsWith(Prefix, StringComparison.Ordinal) &&
            int.TryParse(key.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out id) &&
            id > 0;
    }
}

public static class PageLayouts
{
    /// <summary>
    /// For the stored blob. Nulls are left out, since most sections carry neither a hero flag nor
    /// panels.
    /// </summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Reconciles a stored layout with what this build knows, which is what makes a layout survive a
    /// release that adds or removes a section:
    /// <list type="bullet">
    /// <item>keys this build does not know, and rails that no longer exist, are dropped;</item>
    /// <item>duplicates collapse to their first occurrence;</item>
    /// <item>hero flags and panels are filled in from the definition where missing, and removed
    /// where the section has none;</item>
    /// <item>built-in sections the layout has never seen are appended, on: the user's own order is
    /// the thing worth preserving, and a new section has no business jumping above it;</item>
    /// <item>rails the layout has never seen go after <see cref="PageLayoutDefinition.RailAnchor"/>'s
    /// rule, in <paramref name="railIds"/> order.</item>
    /// </list>
    /// </summary>
    public static List<PageSection> Merge(
        IReadOnlyList<PageSection>? stored, PageLayoutDefinition def, IReadOnlyList<int> railIds)
    {
        var sections = stored ?? [];
        if (def.Upgrade is { } upgrade)
        {
            sections = upgrade(sections);
        }

        var seen = new HashSet<string>();
        var ordered = new List<PageSection>(def.Keys.Count + railIds.Count);

        foreach (var section in sections)
        {
            if (section?.Key is not { } key)
            {
                continue;
            }

            var known = def.Keys.Contains(key) || (LayoutRails.TryParse(key, out var id) && railIds.Contains(id));
            if (known && seen.Add(key))
            {
                ordered.Add(Normalize(section, def));
            }
        }

        foreach (var key in def.Keys)
        {
            if (seen.Add(key))
            {
                ordered.Add(Normalize(new PageSection(key), def));
            }
        }

        foreach (var id in railIds)
        {
            var key = LayoutRails.Key(id);
            if (!seen.Add(key))
            {
                continue;
            }

            ordered.Insert(RailInsertAt(ordered, def.RailAnchor), new PageSection(key));
        }

        return ordered;
    }

    private static int RailInsertAt(List<PageSection> ordered, string? anchor)
    {
        if (anchor is null)
        {
            return ordered.Count;
        }

        var lastRail = ordered.FindLastIndex(s => LayoutRails.TryParse(s.Key, out _));
        if (lastRail >= 0)
        {
            return lastRail + 1;
        }

        var at = ordered.FindIndex(s => s.Key == anchor);
        return at >= 0 ? at : ordered.Count;
    }

    private static PageSection Normalize(PageSection section, PageLayoutDefinition def) => section with
    {
        Hero = def.HeroDefaults.TryGetValue(section.Key, out var hero) ? section.Hero ?? hero : null,
        Panels = def.Panels.TryGetValue(section.Key, out var panels) ? MergePanels(section.Panels, panels) : null,
    };

    /// <summary>A section's panels with unknown ones dropped, duplicates collapsed and missing ones appended, on.</summary>
    public static List<PagePanel> MergePanels(IReadOnlyList<PagePanel>? stored, IReadOnlyList<string> canonical)
    {
        var seen = new HashSet<string>();
        var merged = new List<PagePanel>(canonical.Count);
        foreach (var panel in stored ?? [])
        {
            if (panel?.Key is { } key && canonical.Contains(key) && seen.Add(key))
            {
                merged.Add(panel);
            }
        }

        foreach (var key in canonical)
        {
            if (seen.Add(key))
            {
                merged.Add(new PagePanel(key));
            }
        }

        return merged;
    }
}
