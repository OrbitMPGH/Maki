using Maki.Core.Security;

namespace Maki.Core.Entities;

/// <summary>
/// A user-assigned library label. Deliberately *not* <see cref="Series.Tags"/>: that list is
/// metadata-owned and gets wholesale replaced on every metadata refresh
/// (<c>SeriesMetadataRefreshService</c>), so anything the user typed there would vanish on the
/// next daily job. These live in their own table so they can be renamed, recoloured and deleted
/// library-wide, and so later features (import lists, saved filters) can reference a stable id.
/// </summary>
public class Tag
{
    public int Id { get; set; }

    /// <summary>Unique, compared case-insensitively (NOCASE collation).</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Mantine colour name ("blue", "grape", …) — presentation only.</summary>
    public string Color { get; set; } = "blue";

    public List<Series> Series { get; set; } = [];
}

/// <summary>
/// The series↔tag join, declared explicitly rather than left implicit so it can be read as a plain
/// table. Going through the skip navigation (<c>Series.SelectMany(s =&gt; s.UserTags)</c>) compiles
/// to a SQL APPLY, which SQLite doesn't support.
/// </summary>
public class SeriesTag
{
    public int SeriesId { get; set; }
    public int TagId { get; set; }
}

/// <summary>
/// A named Library filter preset, private to one user. <see cref="Spec"/> is the JSON of the
/// client's filter state (query, status, tags, monitoring, completeness, sort) — the Library grid
/// already filters the full series list client-side, so the server only has to store and hand back
/// the spec.
/// </summary>
public class SavedFilter : IUserOwned
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Spec { get; set; } = "{}";
    public int SortOrder { get; set; }
    public DateTime Created { get; set; }
}
