using System.Globalization;

namespace Maki.Core.Sources;

/// <summary>How <c>SourceMapping.PublisherNumbers</c> stores the publisher's chapter numbers: invariant, ';'-separated.</summary>
public static class PublisherNumbering
{
    public static string? Format(IReadOnlyCollection<decimal>? numbers) =>
        numbers is { Count: > 0 }
            ? string.Join(';', numbers.Distinct().Order().Select(n => n.ToString("0.####", CultureInfo.InvariantCulture)))
            : null;

    public static IReadOnlyList<decimal> Parse(string? stored) =>
        string.IsNullOrEmpty(stored)
            ? []
            : [.. stored.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n : (decimal?)null)
                .OfType<decimal>()];
}
