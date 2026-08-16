using System.Text.Json;

namespace Maki.Core.Configuration;

/// <summary>
/// One saved seed title. <paramref name="Title"/> is a display snapshot, carried so the panel can
/// label a restored seed without a lookup per id — the seeds are arbitrary MangaBaka entries, most
/// of which are not in the library and so have no local title to read. It is cosmetic: only
/// <paramref name="Id"/> is ever sent to the recommendation endpoint, so a title that has since
/// changed upstream costs a stale label and nothing else.
/// </summary>
public record RecommendationSeed(int Id, string? Title = null);

/// <summary>
/// The Discover → Recommended customization panel as one user saved it, so their filters survive a
/// reload instead of being retyped every visit. Mirrors the fields of
/// <c>Maki.Metadata.MangaBaka.RecommendationFilters</c> plus the two things that panel carries which
/// are not filters — the seed titles and the obscurity dial.
/// <para>
/// The server stores and returns this; it never applies it. The client turns a saved spec back into
/// a recommendation request, exactly as it does when the user presses Apply. A field added to
/// <c>RecommendationFilters</c> and not to this record simply cannot be saved as a default — it does
/// not break anything, which is why the two are allowed to be separate shapes.
/// </para>
/// <para>
/// Same discipline as <see cref="HomeLayoutSpec"/> and <c>SavedFilter.Spec</c>: serialize only
/// through <see cref="Json"/>, and never rename a property. A name mismatch does not throw, it
/// silently yields the parameter default — so a rename degrades into "the saved default was
/// forgotten" rather than an error.
/// </para>
/// </summary>
/// <param name="Seeds">MangaBaka entries to base picks on. Empty = the whole library.</param>
/// <param name="MinRating">On the dump's 0–100 scale, like the wire filter — not the slider's 0–10.</param>
/// <param name="Obscurity">-1 (mainstream) … 0 (balanced) … +1 (hidden gems).</param>
/// <param name="Diversity">0 (closest matches) … 1 (spread out). Never negative — unlike obscurity
/// it has no opposite direction; "less diverse than the closest matches" is not a thing.</param>
public record RecommendationDefaultsSpec(
    IReadOnlyList<RecommendationSeed>? Seeds = null,
    int? YearMin = null,
    int? YearMax = null,
    IReadOnlyList<string>? Types = null,
    IReadOnlyList<string>? Statuses = null,
    IReadOnlyList<string>? Genres = null,
    IReadOnlyList<string>? Tags = null,
    int? MinChapters = null,
    int? MaxChapters = null,
    double? MinRating = null,
    double Obscurity = 0,
    double Diversity = 0,
    IReadOnlyList<string>? ContentRatings = null)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Nothing customized — what an unset default reads back as.</summary>
    public static readonly RecommendationDefaultsSpec Empty = new();

    /// <summary>How many entries any one list may carry. A default is a preference, not a payload.</summary>
    private const int MaxListLength = 100;

    /// <summary>
    /// No constraint at all, so storing it would be the same state as having no default. The write
    /// endpoint deletes the row instead, which is what makes "clear the default" reachable through
    /// the same button that sets one.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty =>
        (Seeds?.Count ?? 0) == 0 &&
        YearMin is null && YearMax is null &&
        (Types?.Count ?? 0) == 0 && (Statuses?.Count ?? 0) == 0 &&
        (Genres?.Count ?? 0) == 0 && (Tags?.Count ?? 0) == 0 &&
        MinChapters is null && MaxChapters is null &&
        MinRating is null &&
        Obscurity == 0 && Diversity == 0 && (ContentRatings?.Count ?? 0) == 0;

    /// <summary>
    /// Clamps a client-supplied spec into the ranges the panel can actually produce, so a hand-rolled
    /// request cannot park an unbounded blob in the settings table.
    /// </summary>
    public RecommendationDefaultsSpec Normalize() => this with
    {
        Seeds = Trim(Seeds?.Where(s => s.Id > 0).ToList()),
        Types = TrimNames(Types),
        Statuses = TrimNames(Statuses),
        Genres = TrimNames(Genres),
        Tags = TrimNames(Tags),
        MinRating = MinRating is double r ? Math.Clamp(r, 0, 100) : null,
        Obscurity = Math.Clamp(Obscurity, -1, 1),
        Diversity = Math.Clamp(Diversity, 0, 1),
        ContentRatings = TrimNames(ContentRatings),
    };

    private static IReadOnlyList<T>? Trim<T>(IReadOnlyList<T>? values) =>
        values is null || values.Count == 0 ? null : [.. values.Take(MaxListLength)];

    private static IReadOnlyList<string>? TrimNames(IReadOnlyList<string>? values) =>
        Trim(values?.Where(v => !string.IsNullOrWhiteSpace(v)).ToList());

    /// <summary>Reads a stored blob; null/blank/unreadable JSON reads as "no default".</summary>
    public static RecommendationDefaultsSpec Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        try
        {
            return (JsonSerializer.Deserialize<RecommendationDefaultsSpec>(json, Json) ?? Empty).Normalize();
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    public static string Serialize(RecommendationDefaultsSpec spec) =>
        JsonSerializer.Serialize(spec.Normalize(), Json);
}
