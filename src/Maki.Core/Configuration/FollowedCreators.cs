using System.Text.Json;
using Maki.Core.Recommendations;

namespace Maki.Core.Configuration;

/// <summary>
/// The creators and studios one user follows, stored under <see cref="SettingKeys.DiscoverFollowing"/>.
/// The "New from people you follow" rail is a catalogue browse over exactly this list as a
/// <see cref="CatalogueCredit"/> filter, newest first. Same discipline as the other specs: serialize
/// only through <see cref="Json"/> and never rename a property.
/// </summary>
public record FollowedCreatorsSpec(IReadOnlyList<CatalogueCredit>? Creators = null)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static readonly FollowedCreatorsSpec Empty = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => (Creators?.Count ?? 0) == 0;

    public FollowedCreatorsSpec Normalize() => new(CatalogueCredits.Normalize(Creators));

    public static FollowedCreatorsSpec Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        try
        {
            return (JsonSerializer.Deserialize<FollowedCreatorsSpec>(json, Json) ?? Empty).Normalize();
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    public static string Serialize(FollowedCreatorsSpec spec) =>
        JsonSerializer.Serialize(spec.Normalize(), Json);
}
