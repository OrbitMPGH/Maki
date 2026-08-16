namespace Maki.Core.Sources;

/// <summary>
/// A scrapeable manga site. Implementations live in Maki.Sources and are registered
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

    /// <summary>
    /// Extracts this source's series id from a pasted series-page URL, or null when
    /// the URL isn't on this site or isn't a series page. Lets the UI link a source
    /// directly from a URL without searching.
    /// </summary>
    string? ResolveSeriesIdFromUrl(Uri url) => null;

    /// <summary>
    /// Extra hosts, beyond <see cref="BaseUrl"/>'s own domain, that this source serves cover images
    /// from. Matched as a domain suffix, so naming <c>pstatic.net</c> also permits
    /// <c>webtoon-phinf.pstatic.net</c>.
    /// <para>
    /// This is the allowlist for the cover proxy in <c>SearchController</c>, which fetches a
    /// caller-supplied URL server-side and is therefore an SSRF primitive if left open. Only override
    /// when a source's images live off its own domain — most do not, so most sources need nothing and
    /// adding a source stays "one implementation plus one registration". A blocked host is logged with
    /// its name, so the symptom of a missing entry is a warning line naming exactly what to add, not a
    /// silent hole.
    /// </para>
    /// </summary>
    IReadOnlyList<string> CoverHosts => [];
}

/// <summary>URL-parsing helpers shared by ISource.ResolveSeriesIdFromUrl implementations.</summary>
public static class SourceUrl
{
    /// <summary>
    /// Returns the path remainder after <paramref name="marker"/> when the URL is on the
    /// source's host (www. tolerated), else null. With <paramref name="firstSegmentOnly"/>
    /// the remainder is cut at the next slash (for sites whose ids are a single segment).
    /// </summary>
    public static string? PathTail(Uri url, string baseUrl, string marker, bool firstSegmentOnly = false)
    {
        var baseHost = new Uri(baseUrl).Host;
        if (!url.Host.Equals(baseHost, StringComparison.OrdinalIgnoreCase) &&
            !url.Host.Equals($"www.{baseHost}", StringComparison.OrdinalIgnoreCase) &&
            !baseHost.Equals($"www.{url.Host}", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var path = url.AbsolutePath;
        var index = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var tail = path[(index + marker.Length)..].Trim('/');
        if (tail.Length == 0)
        {
            return null;
        }

        return firstSegmentOnly ? tail.Split('/')[0] : tail;
    }
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
/// ScrambleOffset > 0 marks a MangaFire-style tile-scrambled image; the downloader
/// descrambles it after fetching. XorKeyHex, when set, is a hex-encoded key the
/// downloader XOR-decrypts the fetched bytes with (MangaPlus serves images this way).
/// Data, when set, is already-fetched bytes the downloader writes directly instead of
/// issuing an HTTP request — for a source whose CDN only accepts a real browser's
/// in-page image fetch (e.g. TopManhua, blocked when re-requested by a plain client),
/// so the bytes must be captured during that browser session, not re-fetched by URL.
/// </summary>
public record PageRequest(
    string Url,
    IReadOnlyDictionary<string, string>? Headers = null,
    int ScrambleOffset = 0,
    string? XorKeyHex = null,
    byte[]? Data = null);
