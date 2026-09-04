using System.Text.Json;
using Maki.Core.Metadata;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Maki.Data;

/// <summary>
/// <c>Series.Tags</c> as JSON. Reads both shapes it has ever had, which is what lets the column gain
/// weights and taxonomy paths with no migration: rows written before that are bare string arrays,
/// and they load as names with unknown facets until the series is next refreshed.
/// <para>
/// Deliberately hand-rolled rather than <c>JsonSerializer.Deserialize&lt;List&lt;MetadataTag&gt;&gt;</c>,
/// which throws on the old shape. A tag list is cosmetic; losing the whole column, or worse the
/// whole series load, because one row predates a schema is not a trade worth making.
/// </para>
/// </summary>
internal static class MetadataTagListConverter
{
    public static readonly ValueConverter<List<MetadataTag>, string> Instance = new(
        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
        v => Read(v));

    private static List<MetadataTag> Read(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var tags = new List<MetadataTag>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                switch (element.ValueKind)
                {
                    // The pre-facets shape.
                    case JsonValueKind.String when element.GetString() is { Length: > 0 } name:
                        tags.Add(new MetadataTag(name));
                        break;
                    case JsonValueKind.Object when Str(element, "n") is { Length: > 0 } name:
                        tags.Add(new MetadataTag(name, Str(element, "w"), Str(element, "p")));
                        break;
                }
            }

            return tags;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

internal static class MetadataTagListComparer
{
    public static readonly ValueComparer<List<MetadataTag>> Instance = new(
        (a, b) => (a ?? new List<MetadataTag>()).SequenceEqual(b ?? new List<MetadataTag>()),
        v => v.Aggregate(0, (h, t) => HashCode.Combine(h, t.GetHashCode())),
        v => v.ToList());
}
