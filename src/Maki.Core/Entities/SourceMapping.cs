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

    /// <summary>
    /// Which languages to list chapters in: an ordered comma-separated list of codes ("en,es"), or
    /// null for the source default (English). Read it through <c>SourceLanguages.Parse</c> — it was
    /// a single code before, and a single code is still the usual value.
    /// <para>
    /// Only honoured by sources declaring <see cref="Maki.Core.Sources.SourceCapabilities.SupportsLanguageFilter"/>.
    /// Naming several languages is not free: chapter identity is <c>(Number, Language)</c>, so each
    /// one adds its own row per chapter number and its own wanted/missing count.
    /// </para>
    /// </summary>
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
