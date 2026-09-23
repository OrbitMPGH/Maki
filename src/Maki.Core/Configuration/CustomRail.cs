using System.Text.Json;

namespace Maki.Core.Configuration;

/// <summary>Where a custom rail's rows come from.</summary>
public static class CustomRailSources
{
    /// <summary>The viewer's own series that match the filter.</summary>
    public const string Library = "library";

    /// <summary>The recommender, exactly as the Recommended tab ranks it for the same settings.</summary>
    public const string Recommendations = "recommendations";

    /// <summary>The whole MangaBaka catalogue, filtered and sorted.</summary>
    public const string Catalogue = "catalogue";

    public static readonly string[] All = [Library, Recommendations, Catalogue];
}

/// <summary>
/// Which page a custom rail is drawn on. Stored as <c>SavedFilter.Scope</c> rather than inside the
/// spec, so the Home layout can list a user's rails without parsing every row's JSON.
/// </summary>
public static class CustomRailPlacements
{
    public const string Home = "home";
    public const string Discover = "discover";

    public const string HomeScope = "homerail";
    public const string DiscoverScope = "discoverrail";

    public static string? ScopeFor(string? placement) => placement?.ToLowerInvariant() switch
    {
        Home => HomeScope,
        Discover => DiscoverScope,
        _ => null,
    };

    public static string? PlacementOf(string scope) => scope switch
    {
        HomeScope => Home,
        DiscoverScope => Discover,
        _ => null,
    };

    /// <summary>A library rail only makes sense on Home: Discover is about what the reader doesn't own.</summary>
    public static bool Allows(string placement, string source) =>
        placement == Home || source != CustomRailSources.Library;
}

/// <summary>
/// Row orders per source. The catalogue values are <c>BrowseSort</c>'s strings, passed straight
/// through to the browse path. The recommender has no sort: its order is the ranking.
/// </summary>
public static class CustomRailSorts
{
    public const string Added = "added";
    public const string Read = "read";
    public const string Title = "title";
    public const string Popular = "popular";
    public const string Rating = "rating";
    public const string Newest = "newest";
    public const string Oldest = "oldest";

    private static readonly string[] LibrarySorts = [Added, Read, Title, Popular];
    private static readonly string[] CatalogueSorts = [Popular, Rating, Newest, Oldest];

    public static IReadOnlyList<string> For(string source) => source switch
    {
        CustomRailSources.Library => LibrarySorts,
        CustomRailSources.Catalogue => CatalogueSorts,
        _ => [],
    };

    public static string? DefaultFor(string source) => For(source).FirstOrDefault();
}

/// <summary>
/// A custom rail: a saved catalogue filter plus the source it is evaluated against. Stored as
/// <c>SavedFilter.Spec</c> under a rail scope.
/// <para>
/// Same discipline as <see cref="SearchDefaultsSpec"/>: serialize only through <see cref="Json"/>,
/// and never rename a property. A name mismatch silently yields the parameter default.
/// </para>
/// </summary>
/// <param name="ExcludeOwned">Catalogue only: leave out series already in the library.</param>
/// <param name="Seeds">Recommendations only. Empty means the whole library, like the Recommended tab.</param>
/// <param name="Obscurity">Recommendations only, -1 … 1.</param>
/// <param name="Diversity">Recommendations only, 0 … 1.</param>
public record CustomRailSpec(
    string Source = CustomRailSources.Catalogue,
    SearchDefaultsSpec? Filters = null,
    string? Sort = null,
    bool ExcludeOwned = true,
    IReadOnlyList<RecommendationSeed>? Seeds = null,
    double Obscurity = 0,
    double Diversity = 0)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static readonly CustomRailSpec Empty = new();

    private const int MaxSeeds = 100;

    public CustomRailSpec Normalize()
    {
        var source = Source?.ToLowerInvariant() is { } s && CustomRailSources.All.Contains(s)
            ? s
            : CustomRailSources.Catalogue;
        var sorts = CustomRailSorts.For(source);
        var sort = Sort?.ToLowerInvariant() is { } requested && sorts.Contains(requested)
            ? requested
            : CustomRailSorts.DefaultFor(source);
        var recs = source == CustomRailSources.Recommendations;

        return new CustomRailSpec(
            source,
            (Filters ?? SearchDefaultsSpec.Empty).Normalize(),
            sort,
            source == CustomRailSources.Catalogue && ExcludeOwned,
            recs ? Seeds?.Where(x => x.Id > 0).DistinctBy(x => x.Id).Take(MaxSeeds).ToList() : null,
            recs ? Math.Clamp(double.IsFinite(Obscurity) ? Obscurity : 0, -1, 1) : 0,
            recs ? Math.Clamp(double.IsFinite(Diversity) ? Diversity : 0, 0, 1) : 0);
    }

    public static CustomRailSpec Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty.Normalize();
        }

        try
        {
            return (JsonSerializer.Deserialize<CustomRailSpec>(json, Json) ?? Empty).Normalize();
        }
        catch (JsonException)
        {
            return Empty.Normalize();
        }
    }

    public static string Serialize(CustomRailSpec spec) => JsonSerializer.Serialize(spec.Normalize(), Json);
}
