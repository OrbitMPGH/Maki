using System.Text.Json;

namespace Maki.Core.Configuration;

/// <summary>The canonical section list: every key Home knows, in the order it ships in.</summary>
public static class HomeSections
{
    /// <summary>
    /// The row of labelled numbers at the top: library counts, the reader's progression and what is
    /// waiting to be read, each a panel of its own (<see cref="HomeGlancePanels"/>).
    /// </summary>
    public const string Glance = "glance";

    public const string ContinueReading = "continue";
    public const string Downloading = "downloading";
    public const string RecentlyAdded = "recent";
    public const string JumpBackIn = "jumpback";

    /// <summary>Library series whose anime the reader finished, from the chapter after the anime ends.</summary>
    public const string FromAnime = "fromanime";
    public const string Recommended = "recommended";
    public const string Popular = "popular";

    /// <summary>
    /// Default order. Adding a key here is the only supported way to introduce a section: see
    /// <see cref="PageLayouts.Merge"/> for what existing users' stored layouts do with it.
    /// </summary>
    public static readonly string[] All =
    [
        Glance, Downloading, ContinueReading, JumpBackIn, FromAnime, RecentlyAdded, Recommended, Popular
    ];

    public static bool IsValid(string? key) => key is not null && All.Contains(key);

    public const string RailPrefix = LayoutRails.Prefix;

    public static string RailKey(int id) => LayoutRails.Key(id);

    public static bool TryParseRail(string? key, out int id) => LayoutRails.TryParse(key, out id);
}

/// <summary>
/// The panels of <see cref="HomeSections.Glance"/>. The same strings were top-level section keys
/// before the three shared one section, which is what lets <see cref="HomeLayoutSpec.MigrateGlance"/>
/// carry a stored layout over as a relabel.
/// </summary>
public static class HomeGlancePanels
{
    public const string Stats = "stats";

    /// <summary>The reader's own progression: level, streak, goals and the latest badges.</summary>
    public const string Progress = "progress";

    /// <summary>What is left to read: unread chapters on disk, started against finished series.</summary>
    public const string ToRead = "toread";

    public static readonly string[] All = [Stats, Progress, ToRead];
}

/// <summary>
/// Which Home sections are shown, and in what order, or whether Home exists at all.
/// <para>
/// Same discipline as <see cref="Maki.Core.Reading.ReaderPrefsSpec"/> and <c>SavedFilter.Spec</c>:
/// serialize only through <see cref="Serialize(HomeLayoutSpec, IReadOnlyList{int})"/>, and never
/// rename a property. A name mismatch does not throw, it silently yields the parameter default, so
/// a renamed field degrades into "the user's layout was forgotten" rather than an error.
/// </para>
/// <para>
/// <see cref="Enabled"/> false turns Home off entirely for people who don't read in Maki: the nav
/// drops the tab, <c>/home</c> redirects to the library, and "/" can no longer resolve there.
/// </para>
/// <para>
/// A build older than <see cref="HomeSections.Glance"/> drops that key as unknown and re-appends
/// the three old panel keys, on. That is the whole downgrade story.
/// </para>
/// </summary>
public record HomeLayoutSpec(bool Enabled = true, IReadOnlyList<PageSection>? Sections = null)
{
    public static readonly JsonSerializerOptions Json = PageLayouts.Json;

    public static readonly PageLayoutDefinition Definition = new(
        HomeSections.All,
        new Dictionary<string, bool>
        {
            [HomeSections.ContinueReading] = true,
            [HomeSections.JumpBackIn] = false,
        },
        new Dictionary<string, IReadOnlyList<string>> { [HomeSections.Glance] = HomeGlancePanels.All },
        RailAnchor: null,
        Upgrade: MigrateGlance);

    /// <summary>Every known section, in shipping order, all on.</summary>
    public static HomeLayoutSpec Default => new HomeLayoutSpec().Merge([]);

    /// <summary>
    /// Reconciles a stored layout with this build's sections and the user's Home rails, in their
    /// own order: see <see cref="PageLayouts.Merge"/>. Rails the layout has never seen are appended
    /// after everything else.
    /// </summary>
    public HomeLayoutSpec Merge(IReadOnlyList<int> homeRailIds) =>
        this with { Sections = PageLayouts.Merge(Sections, Definition, homeRailIds) };

    /// <summary><see cref="Merge(IReadOnlyList{int})"/> for a user with no custom rails.</summary>
    public HomeLayoutSpec Merge() => Merge([]);

    /// <summary>
    /// Folds the three glance keys a layout stored before <see cref="HomeSections.Glance"/> existed
    /// into that one section, at the position of whichever came first, carrying their on/off flags
    /// as panel flags. A stored glance section wins over any leftovers. When all three were off the
    /// section is off and its panels all come back on, so switching the section back on shows
    /// something.
    /// </summary>
    public static IReadOnlyList<PageSection> MigrateGlance(IReadOnlyList<PageSection> stored)
    {
        static bool Legacy(PageSection s) => HomeGlancePanels.All.Contains(s.Key);

        if (stored.Any(s => s.Key == HomeSections.Glance))
        {
            return stored.Where(s => !Legacy(s)).ToList();
        }

        var legacy = stored.Where(Legacy).DistinctBy(s => s.Key).ToList();
        if (legacy.Count == 0)
        {
            return stored;
        }

        var enabled = legacy.Any(s => s.Enabled);
        var panels = legacy.Select(s => new PagePanel(s.Key, s.Enabled || !enabled)).ToList();
        var glance = new PageSection(HomeSections.Glance, enabled, Panels: panels);

        var migrated = new List<PageSection>(stored.Count);
        var placed = false;
        foreach (var section in stored)
        {
            if (!Legacy(section))
            {
                migrated.Add(section);
            }
            else if (!placed)
            {
                migrated.Add(glance);
                placed = true;
            }
        }

        return migrated;
    }

    /// <summary>Reads a stored blob, falling back to the default layout for null/blank/bad JSON.</summary>
    public static HomeLayoutSpec Parse(string? json) => Parse(json, []);

    public static HomeLayoutSpec Parse(string? json, IReadOnlyList<int> homeRailIds)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new HomeLayoutSpec().Merge(homeRailIds);
        }

        try
        {
            return (JsonSerializer.Deserialize<HomeLayoutSpec>(json, Json) ?? new HomeLayoutSpec()).Merge(homeRailIds);
        }
        catch (JsonException)
        {
            return new HomeLayoutSpec().Merge(homeRailIds);
        }
    }

    public static string Serialize(HomeLayoutSpec spec) => Serialize(spec, []);

    public static string Serialize(HomeLayoutSpec spec, IReadOnlyList<int> homeRailIds) =>
        JsonSerializer.Serialize(spec.Merge(homeRailIds), Json);
}
