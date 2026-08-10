using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maki.Core.Configuration;

/// <summary>
/// The Discover → search filter panel as one user saved it, so their constraints survive a reload
/// instead of being re-picked every visit. The catalogue-filter half of
/// <c>Maki.Metadata.MangaBaka.RecommendationFilters</c> and nothing else.
/// <para>
/// Deliberately a separate record and a separate setting from <see cref="RecommendationDefaultsSpec"/>,
/// rather than that one stored under a second key. The two panels are not the same panel: seeds, the
/// obscurity dial and the diversity dial are properties of the recommender and mean nothing to a
/// free-text search, and sharing the shape would leave three fields that a hand-rolled request could
/// set and the search would silently ignore. Separate settings also mean saving a search filter does
/// not quietly rewrite somebody's Recommended defaults.
/// </para>
/// <para>
/// Same discipline as <see cref="RecommendationDefaultsSpec"/>, <see cref="HomeLayoutSpec"/> and
/// <c>SavedFilter.Spec</c>: serialize only through <see cref="Json"/>, and never rename a property.
/// A name mismatch does not throw, it silently yields the parameter default — so a rename degrades
/// into "the saved default was forgotten" rather than into an error.
/// </para>
/// </summary>
/// <param name="MinRating">On the dump's 0–100 scale, like the wire filter — not the slider's 0–10.</param>
public record SearchDefaultsSpec(
    int? YearMin = null,
    int? YearMax = null,
    IReadOnlyList<string>? Types = null,
    IReadOnlyList<string>? Statuses = null,
    IReadOnlyList<string>? Genres = null,
    IReadOnlyList<string>? Tags = null,
    int? MinChapters = null,
    int? MaxChapters = null,
    double? MinRating = null,
    IReadOnlyList<string>? ContentRatings = null)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Nothing constrained — what an unset default reads back as.</summary>
    public static readonly SearchDefaultsSpec Empty = new();

    /// <summary>How many entries any one list may carry. A default is a preference, not a payload.</summary>
    private const int MaxListLength = 100;

    /// <summary>
    /// No constraint at all, so storing it would be the same state as having no default. The write
    /// endpoint deletes the row instead, which is what makes "clear the default" reachable through
    /// the same button that sets one.
    /// </summary>
    [JsonIgnore]
    public bool IsEmpty =>
        YearMin is null && YearMax is null &&
        (Types?.Count ?? 0) == 0 && (Statuses?.Count ?? 0) == 0 &&
        (Genres?.Count ?? 0) == 0 && (Tags?.Count ?? 0) == 0 &&
        MinChapters is null && MaxChapters is null &&
        MinRating is null && (ContentRatings?.Count ?? 0) == 0;

    /// <summary>
    /// Clamps a client-supplied spec into the ranges the panel can actually produce, so a
    /// hand-rolled request cannot park an unbounded blob in the settings table.
    /// </summary>
    public SearchDefaultsSpec Normalize() => this with
    {
        Types = TrimNames(Types),
        Statuses = TrimNames(Statuses),
        Genres = TrimNames(Genres),
        Tags = TrimNames(Tags),
        MinRating = MinRating is double r ? Math.Clamp(r, 0, 100) : null,
        ContentRatings = TrimNames(ContentRatings),
    };

    private static IReadOnlyList<string>? TrimNames(IReadOnlyList<string>? values)
    {
        var kept = values?.Where(v => !string.IsNullOrWhiteSpace(v)).Take(MaxListLength).ToList();
        return kept is null || kept.Count == 0 ? null : kept;
    }

    /// <summary>Reads a stored blob; null/blank/unreadable JSON reads as "no default".</summary>
    public static SearchDefaultsSpec Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        try
        {
            return (JsonSerializer.Deserialize<SearchDefaultsSpec>(json, Json) ?? Empty).Normalize();
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    public static string Serialize(SearchDefaultsSpec spec) =>
        JsonSerializer.Serialize(spec.Normalize(), Json);
}
