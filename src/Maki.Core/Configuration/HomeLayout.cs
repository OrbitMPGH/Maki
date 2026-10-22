using System.Text.Json;

namespace Maki.Core.Configuration;

/// <summary>The canonical section list: every key Home knows, in the order it ships in.</summary>
public static class HomeSections
{
    /// <summary>
    /// A retired section key. The reader's last five weeks used to be a section of its own, first
    /// with three panels (<see cref="HomeGlancePanels"/>), then as one card riding in the Continue
    /// hero when it sat next to it. It is now <see cref="HomeHeroOptions.Glance"/>, a switch on
    /// Continue reading; the key is kept so <see cref="HomeLayoutSpec.MigrateGlanceIntoContinue"/>
    /// can read layouts stored by either build.
    /// </summary>
    public const string Glance = "glance";

    public const string ContinueReading = "continue";
    public const string Downloading = "downloading";
    public const string RecentlyAdded = "recent";
    public const string JumpBackIn = "jumpback";

    /// <summary>Library series whose anime the reader finished, from the chapter after the anime ends.</summary>
    public const string FromAnime = "fromanime";
    public const string Recommended = "recommended";

    /// <summary>The newest titles from the creators and studios the reader follows.</summary>
    public const string Following = "following";
    public const string Popular = "popular";

    /// <summary>
    /// Default order. Adding a key here is the only supported way to introduce a section: see
    /// <see cref="PageLayouts.Merge"/> for what existing users' stored layouts do with it.
    /// </summary>
    public static readonly string[] All =
    [
        ContinueReading, JumpBackIn, FromAnime, RecentlyAdded, Downloading, Following, Recommended, Popular
    ];

    /// <summary>
    /// Discovery lives on Discover. Home ships without these two and a reader can still switch them
    /// on in the layout editor.
    /// </summary>
    public static readonly string[] DefaultOff = [Recommended, Popular];

    /// <summary>
    /// The shipping order before Home opened on its hero, which <see cref="HomeLayoutSpec.MigrateHeroFirst"/>
    /// recognises. Without <see cref="Glance"/>, which led that order: it has been folded into
    /// Continue reading by the time the move runs.
    /// </summary>
    internal static readonly string[] HeroFirstPredecessor =
    [
        Downloading, ContinueReading, JumpBackIn, FromAnime, RecentlyAdded, Following, Recommended, Popular
    ];

    public static bool IsValid(string? key) => key is not null && All.Contains(key);

    public const string RailPrefix = LayoutRails.Prefix;

    public static string RailKey(int id) => LayoutRails.Key(id);

    public static bool TryParseRail(string? key, out int id) => LayoutRails.TryParse(key, out id);
}

/// <summary>The switches on the reading sections that lead with a hero (<see cref="PageSection.Options"/>).</summary>
public static class HomeHeroOptions
{
    /// <summary>
    /// The reader's last five weeks, as a card in the hero's right column. Continue reading only:
    /// one glance per page, and that section leads it.
    /// </summary>
    public const string Glance = "glance";

    /// <summary>
    /// Next up as a list in a card beside the hero, at the hero's height, instead of a tray of
    /// posters under it. Off by default: the tray shows covers, which is how readers pick a series.
    /// </summary>
    public const string NextUpBeside = "nextUpBeside";

    public static readonly IReadOnlyDictionary<string, bool> ContinueDefaults =
        new Dictionary<string, bool> { [Glance] = true, [NextUpBeside] = false };

    public static readonly IReadOnlyDictionary<string, bool> JumpBackDefaults =
        new Dictionary<string, bool> { [NextUpBeside] = false };
}

/// <summary>
/// The three panels <see cref="HomeSections.Glance"/> held until it became one card, and before that
/// three top-level section keys. Kept only so <see cref="HomeLayoutSpec.MigrateGlance"/> and
/// <see cref="HomeLayoutSpec.MigrateGlancePanels"/> can read layouts stored by either build.
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
/// A build that still had the glance as a section drops the Continue options as unknown JSON and
/// re-appends its own glance key, on. That is the whole downgrade story.
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
        new Dictionary<string, IReadOnlyList<string>>(),
        RailAnchor: null,
        Upgrade: stored => MigrateHeroFirst(MigrateGlanceIntoContinue(MigrateGlancePanels(MigrateGlance(stored)))),
        DefaultOff: HomeSections.DefaultOff,
        Options: new Dictionary<string, IReadOnlyDictionary<string, bool>>
        {
            [HomeSections.ContinueReading] = HomeHeroOptions.ContinueDefaults,
            [HomeSections.JumpBackIn] = HomeHeroOptions.JumpBackDefaults,
        });

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

    /// <summary>
    /// A glance stored with every panel switched off showed nothing, so its owner did not want it:
    /// it comes through as the section switched off. Any other panel flags are dropped by the merge,
    /// since the section no longer has panels.
    /// </summary>
    public static IReadOnlyList<PageSection> MigrateGlancePanels(IReadOnlyList<PageSection> stored) =>
        stored
            .Select(s => s.Key == HomeSections.Glance && s.Panels is { Count: > 0 } panels && panels.All(p => !p.Enabled)
                ? s with { Enabled = false }
                : s)
            .ToList();

    /// <summary>
    /// Folds a stored glance section into Continue reading's <see cref="HomeHeroOptions.Glance"/>
    /// switch, carrying its on/off flag, and drops the section. Its position is forgotten: the glance
    /// now lives in the Continue hero wherever that sits. With no Continue section stored the flag
    /// has nowhere to go and the default (on) applies when the merge appends one.
    /// </summary>
    public static IReadOnlyList<PageSection> MigrateGlanceIntoContinue(IReadOnlyList<PageSection> stored)
    {
        var glance = stored.FirstOrDefault(s => s.Key == HomeSections.Glance);
        if (glance is null)
        {
            return stored;
        }

        return stored
            .Where(s => s.Key != HomeSections.Glance)
            .Select(s => s.Key == HomeSections.ContinueReading
                ? s with { Options = WithOption(s.Options, HomeHeroOptions.Glance, glance.Enabled) }
                : s)
            .ToList();
    }

    private static Dictionary<string, bool> WithOption(IReadOnlyDictionary<string, bool>? options, string key, bool value)
    {
        var next = options is null ? new Dictionary<string, bool>() : new Dictionary<string, bool>(options);
        next[key] = value;
        return next;
    }

    /// <summary>
    /// Moves a layout still in the previous default order onto the current shipping order. That
    /// order is the one nobody chose, so only the switches the owner flipped (a section off, a hero
    /// off) are theirs, and those travel with each section. A layout in any other
    /// order is the owner's own arrangement and is left alone. Custom rails stay after the built-in
    /// sections.
    /// </summary>
    public static IReadOnlyList<PageSection> MigrateHeroFirst(IReadOnlyList<PageSection> stored)
    {
        var builtIn = stored.Where(s => !LayoutRails.TryParse(s.Key, out _)).ToList();
        if (!builtIn.Select(s => s.Key).SequenceEqual(HomeSections.HeroFirstPredecessor))
        {
            return stored;
        }

        var migrated = HomeSections.All.Select(key => builtIn.First(s => s.Key == key)).ToList();
        migrated.AddRange(stored.Where(s => LayoutRails.TryParse(s.Key, out _)));
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
