using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Maki.Metadata.MangaBaka;

/// <summary>
/// Optional constraints applied to recommendation candidates, ANDed onto the scan query.
/// Empty fields mean "no constraint". Types/statuses are the dump's raw values
/// (e.g. type "manga"/"manhwa", status "completed"/"releasing"). <see cref="Tags"/> are
/// tags_v2 vocabulary names; they're matched per-candidate in C# (not in SQL) because the
/// scans already read each candidate's tags — see the two GetSimilarAsync implementations.
/// </summary>
public record RecommendationFilters(
    int? YearMin = null,
    int? YearMax = null,
    IReadOnlyList<string>? Types = null,
    IReadOnlyList<string>? Statuses = null,
    double? MinRating = null,
    IReadOnlyList<string>? Genres = null,
    int? MinChapters = null,
    int? MaxChapters = null,
    IReadOnlyList<string>? Tags = null)
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

        // total_chapters is TEXT and may be fractional; CAST for a numeric compare. Rows with a
        // null/blank count fall out of a bounded range, which is the sensible thing for a filter.
        if (MinChapters is int cmin)
        {
            parts.Add($"CAST({alias}.total_chapters AS REAL) >= ${prefix}_cmin");
            cmd.Parameters.AddWithValue($"${prefix}_cmin", cmin);
        }

        if (MaxChapters is int cmax)
        {
            parts.Add($"CAST({alias}.total_chapters AS REAL) <= ${prefix}_cmax");
            cmd.Parameters.AddWithValue($"${prefix}_cmax", cmax);
        }

        // genres is a JSON array of quoted strings; a case-insensitive LIKE on the quoted name is
        // an exact membership test. All selected genres must be present (AND).
        if (Genres is { Count: > 0 })
        {
            for (var i = 0; i < Genres.Count; i++)
            {
                var name = $"${prefix}_g{i.ToString(CultureInfo.InvariantCulture)}";
                parts.Add($"{alias}.genres LIKE {name}");
                cmd.Parameters.AddWithValue(name, $"%\"{Genres[i]}\"%");
            }
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
