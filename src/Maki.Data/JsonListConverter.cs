using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Maki.Data;

/// <summary>
/// Stores a List&lt;T&gt; of records as JSON text, enums by name so a reordered enum cannot silently
/// change what a stored row means. A row that fails to parse reads as an empty list.
/// </summary>
internal static class JsonListConverter<T> where T : class
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static readonly ValueConverter<List<T>, string> Instance = new(
        v => JsonSerializer.Serialize(v, Options),
        v => Read(v));

    public static readonly ValueComparer<List<T>> Comparer = new(
        (a, b) => (a ?? new List<T>()).SequenceEqual(b ?? new List<T>()),
        v => v.Aggregate(0, (h, x) => HashCode.Combine(h, x == null ? 0 : x.GetHashCode())),
        v => v.ToList());

    private static List<T> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<T>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
