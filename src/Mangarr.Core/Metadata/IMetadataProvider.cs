using Mangarr.Core.Entities;

namespace Mangarr.Core.Metadata;

public interface IMetadataProvider
{
    /// <summary>Stable lowercase key, e.g. "mangabaka".</summary>
    string Name { get; }

    Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(string query, CancellationToken ct = default);

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
    public string? Description { get; init; }
    public string? CoverUrl { get; init; }
    public int? Year { get; init; }
    public SeriesStatus Status { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string? AuthorStory { get; init; }
    public string? AuthorArt { get; init; }
    public int? TotalChapters { get; init; }
    public int? TotalVolumes { get; init; }
    public string? WebUrl { get; init; }

    // Cross-provider IDs
    public int? MangaBakaId { get; init; }
    public int? AniListId { get; init; }
    public int? MalId { get; init; }
    public string? MangaUpdatesId { get; init; }
    public string? MangaDexUuid { get; init; }
}
