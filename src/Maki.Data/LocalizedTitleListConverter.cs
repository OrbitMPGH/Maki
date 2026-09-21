using System.Text.Json;
using Maki.Core.Entities;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Maki.Data;

/// <summary>
/// Stores List&lt;LocalizedTitle&gt; columns as JSON text, the same way
/// <see cref="StringListConverter"/> stores the plain lists beside them.
/// <para>
/// Reads are deliberately lenient about the element shape. The column held a bare
/// <c>["a","b"]</c> before titles carried a language, and a strict
/// <c>Deserialize&lt;List&lt;LocalizedTitle&gt;&gt;</c> throws on every one of those rows — which is a
/// crash on read, not a missing field. The <c>AddSeriesAltTitleLanguages</c> migration rewrites the
/// stored rows, so this path only has to cover a database restored from an older backup or edited
/// by hand; it costs one <c>ValueKind</c> check per element.
/// </para>
/// </summary>
internal static class LocalizedTitleListConverter
{
    public static readonly ValueConverter<List<LocalizedTitle>, string> Instance = new(
        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
        v => Read(v));

    internal static List<LocalizedTitle> Read(string? json)
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

            var titles = new List<LocalizedTitle>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                switch (element.ValueKind)
                {
                    case JsonValueKind.String:
                        var bare = element.GetString();
                        if (!string.IsNullOrWhiteSpace(bare))
                        {
                            titles.Add(new LocalizedTitle(bare, null));
                        }

                        break;

                    case JsonValueKind.Object:
                        var title = Property(element, "Title");
                        if (!string.IsNullOrWhiteSpace(title))
                        {
                            titles.Add(new LocalizedTitle(title, Property(element, "Language")));
                        }

                        break;
                }
            }

            return titles;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Case-insensitive so a row written by a differently-cased serializer still reads.</summary>
    private static string? Property(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals(name) ||
                string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            }
        }

        return null;
    }
}

internal static class LocalizedTitleListComparer
{
    public static readonly ValueComparer<List<LocalizedTitle>> Instance = new(
        (a, b) => (a ?? new List<LocalizedTitle>()).SequenceEqual(b ?? new List<LocalizedTitle>()),
        v => v.Aggregate(0, (h, t) => HashCode.Combine(h, t.GetHashCode())),
        v => v.ToList());
}
