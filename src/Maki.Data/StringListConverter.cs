using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging;

namespace Maki.Data;

/// <summary>Stores List&lt;string&gt; columns as JSON text in SQLite. A value that fails to parse reads as an empty list.</summary>
internal static class StringListConverter
{
    public static readonly ValueConverter<List<string>, string> Instance = new(
        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
        v => Read(v));

    private static List<string> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, (JsonSerializerOptions?)null) ?? [];
        }
        catch (JsonException ex)
        {
            DataDiagnostics.Logger?.LogWarning(ex,
                "Stored string list could not be read and was treated as empty; saving this row would overwrite it");
            return [];
        }
    }
}

internal static class StringListComparer
{
    public static readonly ValueComparer<List<string>> Instance = new(
        (a, b) => (a ?? new List<string>()).SequenceEqual(b ?? new List<string>()),
        v => v.Aggregate(0, (h, s) => HashCode.Combine(h, s == null ? 0 : s.GetHashCode())),
        v => v.ToList());
}
