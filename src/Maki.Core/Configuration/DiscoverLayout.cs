using System.Text.Json;

namespace Maki.Core.Configuration;

/// <summary>The sections of Discover's Browse tab, in the order they ship in.</summary>
public static class DiscoverSections
{
    public const string Hero = "hero";
    public const string Taste = "taste";
    public const string RecentActivity = "recent";
    public const string SideInterests = "sideinterests";
    public const string Cohort = "cohort";
    public const string Trending = "trending";
    public const string Catalogue = "catalogue";
    public const string Genres = "genres";

    public static readonly string[] All =
        [Hero, Taste, RecentActivity, SideInterests, Cohort, Trending, Catalogue, Genres];
}

/// <summary>
/// How the Discover Browse tab is arranged: the built-in sections and the user's Discover rails.
/// Same discipline as <see cref="HomeLayoutSpec"/>. A rail the layout has never seen goes after
/// the last rail already placed, or right before Trending, which is where rails rendered before
/// Discover had a layout at all, so a user's first layout keeps their page as it was.
/// </summary>
public record DiscoverLayoutSpec(IReadOnlyList<PageSection>? Sections = null)
{
    public static readonly PageLayoutDefinition Definition = new(
        DiscoverSections.All,
        new Dictionary<string, bool>(),
        new Dictionary<string, IReadOnlyList<string>>(),
        RailAnchor: DiscoverSections.Trending);

    public static DiscoverLayoutSpec Default => new DiscoverLayoutSpec().Merge([]);

    public DiscoverLayoutSpec Merge(IReadOnlyList<int> discoverRailIds) =>
        this with { Sections = PageLayouts.Merge(Sections, Definition, discoverRailIds) };

    public static DiscoverLayoutSpec Parse(string? json, IReadOnlyList<int> discoverRailIds)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new DiscoverLayoutSpec().Merge(discoverRailIds);
        }

        try
        {
            return (JsonSerializer.Deserialize<DiscoverLayoutSpec>(json, PageLayouts.Json) ?? new DiscoverLayoutSpec())
                .Merge(discoverRailIds);
        }
        catch (JsonException)
        {
            return new DiscoverLayoutSpec().Merge(discoverRailIds);
        }
    }

    public static string Serialize(DiscoverLayoutSpec spec, IReadOnlyList<int> discoverRailIds) =>
        JsonSerializer.Serialize(spec.Merge(discoverRailIds), PageLayouts.Json);
}
