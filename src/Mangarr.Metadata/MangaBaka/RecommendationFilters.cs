using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Mangarr.Metadata.MangaBaka;

/// <summary>
/// Optional constraints applied to recommendation candidates, ANDed onto the scan query.
/// Empty fields mean "no constraint". Types/statuses are the dump's raw values
/// (e.g. type "manga"/"manhwa", status "completed"/"releasing").
/// </summary>
public record RecommendationFilters(
    int? YearMin = null,
    int? YearMax = null,
    IReadOnlyList<string>? Types = null,
    IReadOnlyList<string>? Statuses = null,
    double? MinRating = null)
{
    public static readonly RecommendationFilters None = new();

    /// <summary>
    /// Appends parameters to <paramref name="cmd"/> and returns the SQL fragment (leading
    /// " AND …") to splice into the candidate scan's WHERE, qualified by <paramref name="alias"/>.
    /// A distinct <paramref name="prefix"/> keeps parameter names unique if called twice.
    /// </summary>
    public string BuildClause(SqliteCommand cmd, string alias, string prefix = "f")
    {
        var parts = new List<string>();

        if (YearMin is int ymin)
        {
            parts.Add($"{alias}.year >= ${prefix}_ymin");
            cmd.Parameters.AddWithValue($"${prefix}_ymin", ymin);
        }

        if (YearMax is int ymax)
        {
            parts.Add($"{alias}.year <= ${prefix}_ymax");
            cmd.Parameters.AddWithValue($"${prefix}_ymax", ymax);
        }

        if (MinRating is double mr)
        {
            parts.Add($"{alias}.rating >= ${prefix}_mr");
            cmd.Parameters.AddWithValue($"${prefix}_mr", mr);
        }

        AppendIn(cmd, parts, alias, "type", Types, $"{prefix}_t");
        AppendIn(cmd, parts, alias, "status", Statuses, $"{prefix}_s");

        return parts.Count > 0 ? " AND " + string.Join(" AND ", parts) : string.Empty;
    }

    private static void AppendIn(
        SqliteCommand cmd, List<string> parts, string alias, string column,
        IReadOnlyList<string>? values, string prefix)
    {
        if (values is null || values.Count == 0)
        {
            return;
        }

        var names = new List<string>(values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            var name = $"${prefix}{i.ToString(CultureInfo.InvariantCulture)}";
            names.Add(name);
            cmd.Parameters.AddWithValue(name, values[i]);
        }

        parts.Add($"{alias}.{column} IN ({string.Join(",", names)})");
    }
}
