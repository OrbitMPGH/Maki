using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Maki.Data;

/// <summary>Stores List&lt;string&gt; columns as JSON text in SQLite.</summary>
internal static class StringListConverter
{
    public static readonly ValueConverter<List<string>, string> Instance = new(
        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
        v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>());
}

internal static class StringListComparer
{
    public static readonly ValueComparer<List<string>> Instance = new(
        (a, b) => (a ?? new List<string>()).SequenceEqual(b ?? new List<string>()),
        v => v.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode())),
        v => v.ToList());
}
