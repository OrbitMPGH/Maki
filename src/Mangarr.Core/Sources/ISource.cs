namespace Mangarr.Core.Sources;

/// <summary>
/// A scrapeable manga site. Implementations live in Mangarr.Sources and are registered
/// in DI as IEnumerable&lt;ISource&gt;; a future plugin loader only needs to add registrations.
/// </summary>
public interface ISource
{
    /// <summary>Stable lowercase key, e.g. "mangadex". Persisted in SourceMapping.SourceName.</summary>
    string Name { get; }

    /// <summary>Human-readable display name, e.g. "MangaDex".</summary>
    string DisplayName { get; }

    /// <summary>Site base URL, used for UI links and default Referer.</summary>
    string BaseUrl { get; }

    SourceCapabilities Capabilities { get; }

    Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default);

    Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default);

    Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default);

    /// <summary>
    /// Resolves page image URLs for a chapter. Must be called at download time, not enqueue
    /// time — some sources (MangaDex at-home) return short-lived URLs.
    /// </summary>
    Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default);
}

[Flags]
public enum SourceCapabilities
{
    None = 0,
    NeedsFlareSolverr = 1,
    SupportsLanguageFilter = 2
}

/// <summary>A search hit on the source site.</summary>
public record SourceSeriesResult(
    string SourceSeriesId,
    string Title,
    string Url,
    string? CoverUrl = null,
    string? Description = null);

/// <summary>Full series info as the source presents it.</summary>
public record SourceSeriesDetail(
    string SourceSeriesId,
    string Title,
    string Url,
    string? CoverUrl = null,
    string? Description = null,
    string? Status = null);

/// <summary>A chapter as listed by the source.</summary>
public record SourceChapter(
    string SourceName,
    string SourceSeriesId,
    string SourceChapterId,
    string? NumberRaw,
    decimal? Number,
    int? Volume,
    string? Title,
    string Language,
    DateTime? ReleaseDate,
    string? Url = null);

/// <summary>Resolved page list for a chapter.</summary>
public record ChapterPages(IReadOnlyList<PageRequest> Pages);

/// <summary>
/// A single page image fetch. Headers carry Referer/User-Agent/cookie requirements
/// end-to-end to the downloader — never fetch a page URL without its headers.
/// </summary>
public record PageRequest(string Url, IReadOnlyDictionary<string, string>? Headers = null);
