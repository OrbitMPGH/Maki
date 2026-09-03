using System.Text.Json.Serialization;

namespace Maki.Core.Entities;

/// <summary>Links a Series to a scrapeable site source. A series can have several.</summary>
public class SourceMapping
{
    public int Id { get; set; }
    public int SeriesId { get; set; }
    public Series? Series { get; set; }

    /// <summary>Stable source key, e.g. "mangadex", "mangapill".</summary>
    public string SourceName { get; set; } = string.Empty;

    /// <summary>The series identifier within the source (UUID, slug, ...).</summary>
    public string SourceSeriesId { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    /// <summary>Restrict chapters to this language; null = source default.</summary>
    public string? LanguageFilter { get; set; }

    /// <summary>Lower wins when the same chapter is available from multiple mappings.</summary>
    public int Priority { get; set; } = 1;

    public bool Enabled { get; set; } = true;
    public DateTime? LastRefresh { get; set; }
    public string? LastError { get; set; }

    /// <summary>
    /// When this mapping's <see cref="ChapterSourceLink"/> snapshot was last replaced from a
    /// successful source listing. Null means cleanup cannot safely rely on this mapping yet.
    /// </summary>
    public DateTime? ChapterSnapshotAt { get; set; }

    [JsonIgnore]
    public List<ChapterSourceLink> ChapterLinks { get; set; } = [];

    /// <summary>How this mapping was matched. See <see cref="SourceMappingOrigin"/>.</summary>
    public SourceMappingOrigin Origin { get; set; } = SourceMappingOrigin.Unknown;
}
