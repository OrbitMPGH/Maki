using System.Text.Json;
using Maki.Core.Recommendations;

namespace Maki.Core.Configuration;

/// <summary>
/// One user's never-show list: genres and tags removed from every Discover surface (rails, search,
/// the catalogue browser, creator pages and recommendations) whatever the filter panel says. Stored
/// under <see cref="SettingKeys.DiscoverHidden"/>. Same discipline as the other specs: serialize
/// only through <see cref="Json"/> and never rename a property.
/// </summary>
public record HiddenContentSpec(IReadOnlyList<CatalogueTerm>? Terms = null)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static readonly HiddenContentSpec Empty = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => (Terms?.Count ?? 0) == 0;

    public HiddenContentSpec Normalize() => new(CatalogueRules.NormalizeTerms(Terms));

    public static HiddenContentSpec Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        try
        {
            return (JsonSerializer.Deserialize<HiddenContentSpec>(json, Json) ?? Empty).Normalize();
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    public static string Serialize(HiddenContentSpec spec) =>
        JsonSerializer.Serialize(spec.Normalize(), Json);
}
