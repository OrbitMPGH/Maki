using Maki.Core.Entities;

namespace Maki.Core.Metadata;

public interface IMetadataProvider
{
    /// <summary>Stable lowercase key, e.g. "mangabaka".</summary>
    string Name { get; }

    /// <summary>
    /// Searches the provider, dropping anything above <paramref name="maxContentRating"/>.
    /// <para>
    /// The ceiling is a required argument rather than something the provider looks up, because it is
    /// a property of the <em>caller</em> (see <c>ICurrentUser.MaxContentRating</c>) and providers are
    /// singletons with no current user. Passing it explicitly is what forces every call site to say
    /// whose ceiling applies, and stops the filter quietly reverting to an instance-wide default.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(
        string query, string maxContentRating, CancellationToken ct = default);

    Task<SeriesMetadata?> GetAsync(string providerId, CancellationToken ct = default);
}

public record MetadataSearchResult(
    string ProviderId,
    string Title,
    string? CoverUrl,
    int? Year,
    SeriesStatus Status,
    string? Description,
    int? TotalChapters);

public record SeriesMetadata
{
    public required string ProviderId { get; init; }
    public required string Title { get; init; }
    public string? OriginalTitle { get; init; }
    /// <summary>
    /// Other primary titles besides <see cref="Title"/> (English) and <see cref="OriginalTitle"/>
    /// (native script) — e.g. romanized or other-language primary titles.
    /// </summary>
    public IReadOnlyList<string> AltTitles { get; init; } = [];
    public string? Description { get; init; }
    public string? CoverUrl { get; init; }
    public int? Year { get; init; }
    public SeriesStatus Status { get; init; }

    /// <summary>
    /// One of <see cref="SeriesTypes"/>, or null when the provider did not say. Drives
    /// reading-profile auto-selection, so it is normalized on the way in rather than at every read.
    /// </summary>
    public string? Type { get; init; }

    public IReadOnlyList<string> Genres { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>One of the "safe"/"suggestive"/"erotica"/"pornographic" vocabulary, or null when the provider did not say.</summary>
    public string? ContentRating { get; init; }
    public string? AuthorStory { get; init; }
    public string? AuthorArt { get; init; }
    public string? Publisher { get; init; }
    public int? TotalChapters { get; init; }
    public int? TotalVolumes { get; init; }
    public string? WebUrl { get; init; }
    public bool HasAnime { get; init; }
    public string? AnimeName { get; init; }
    public string? AnimeStart { get; init; }
    public string? AnimeEnd { get; init; }

    // Cross-provider IDs
    public int? MangaBakaId { get; init; }
    public int? AniListId { get; init; }
    public int? MalId { get; init; }
    public int? KitsuId { get; init; }
    public string? MangaUpdatesId { get; init; }
    public string? MangaDexUuid { get; init; }
}
