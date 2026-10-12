using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Maki.Core.Parsing;
using Maki.Core.Sources;

namespace Maki.Sources.Suwayomi;

/// <summary>
/// One source of the admin's Suwayomi server, exposed to Maki as if it were a built-in one.
/// <para>
/// Ids are the portable ones: <c>SourceSeriesId</c> is the manga's source-relative <c>url</c> and
/// <c>SourceChapterId</c> the chapter's, the keys Mihon itself restores backups by. Suwayomi's own
/// ids are local to one instance and are looked up, and cached, on every use. A manga this instance
/// has never seen cannot be looked up at all (there is no resolve-by-url call), so it fails with
/// the fix in the message rather than being guessed at by title.
/// </para>
/// </summary>
public sealed partial class SuwayomiSource(
    SuwayomiExposedSource exposed,
    string chapterLanguage,
    Uri baseUri,
    SuwayomiClient client,
    SuwayomiMangaIdCache cache) : ISource, IPageClientSource
{
    public const string NamePrefix = "suwayomi-";

    /// <summary>The Mihon source id, as Suwayomi sends it.</summary>
    public string SourceId { get; } = exposed.Id;

    public string Name { get; } = NamePrefix + exposed.Id;

    public string DisplayName { get; } =
        string.IsNullOrWhiteSpace(exposed.DisplayName) ? exposed.Name : exposed.DisplayName;

    public string BaseUrl { get; } = baseUri.GetLeftPart(UriPartial.Authority) + baseUri.AbsolutePath.TrimEnd('/');

    public SourceCapabilities Capabilities => SourceCapabilities.None;

    public SourceRating Rating { get; } = exposed.ContentWarning?.ToUpperInvariant() switch
    {
        "NSFW" => SourceRating.Adult,
        "MIXED" => SourceRating.Mature,
        null when exposed.IsNsfw => SourceRating.Adult,
        _ => SourceRating.General
    };

    public IReadOnlyList<string> SupportedLanguages { get; } = [chapterLanguage];

    public string PageHttpClientName => SuwayomiClient.PagesHttpClientName;

    public bool IsPageUrlAllowed(Uri url) =>
        url.IsAbsoluteUri &&
        string.Equals(url.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(url.IdnHost, baseUri.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        url.Port == baseUri.Port;

    /// <summary>The Maki endpoint that relays a Suwayomi thumbnail, since covers on a LAN host can't go through the cover proxy.</summary>
    public static string ThumbnailPath(string sourceId, int mangaId) =>
        $"/api/v1/suwayomi/thumbnail/{sourceId}/{mangaId.ToString(CultureInfo.InvariantCulture)}";

    public async Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        await client.EnsureInstalledAsync(SourceId, ct);
        var connection = await client.ConnectionAsync(ct);
        var results = await client.SearchAsync(SourceId, title, ct);
        return [.. results
            .Where(m => m.Url.Length > 0)
            .Select(m => new SourceSeriesResult(
                m.Url, m.Title, SeriesUrl(connection, m), CoverUrl(m), m.Description, Author: m.Author))];
    }

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        await client.EnsureInstalledAsync(SourceId, ct);
        var connection = await client.ConnectionAsync(ct);
        var (_, refresh) = await RefreshAsync(connection, sourceSeriesId, manga: true, chapters: false, ct);
        var manga = refresh.Manga!;
        return new SourceSeriesDetail(
            sourceSeriesId, manga.Title, SeriesUrl(connection, manga), CoverUrl(manga), manga.Description,
            manga.Status?.ToLowerInvariant());
    }

    /// <param name="languageFilter">Ignored: a Mihon source publishes one language, or the one the admin assigned it.</param>
    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        await client.EnsureInstalledAsync(SourceId, ct);
        var connection = await client.ConnectionAsync(ct);
        var (mangaId, refresh) = await RefreshAsync(connection, sourceSeriesId, manga: false, chapters: true, ct);
        var chapters = refresh.Chapters!.Where(c => c.Url.Length > 0).ToList();
        foreach (var chapter in chapters)
        {
            cache.SetChapter(connection.BaseUri, mangaId, chapter.Url, chapter.Id);
        }

        // Extensions such as MangaDex list one entry per scanlation group; the newest upload wins,
        // then the earliest in the source's own order, rather than whichever the extension sorted first.
        return SourceChapterList.Normalize(
            chapters,
            c => ToSourceChapter(c, sourceSeriesId),
            group => group.OrderByDescending(c => c.UploadDate).ThenBy(c => c.SourceOrder).First());
    }

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        await client.EnsureInstalledAsync(SourceId, ct);
        var connection = await client.ConnectionAsync(ct);
        var mangaWasCached = cache.Manga(connection.BaseUri, SourceId, chapter.SourceSeriesId) is not null;
        var mangaId = await MangaIdAsync(connection, chapter.SourceSeriesId, fresh: false, ct);
        var chapterId = await ChapterIdAsync(connection, mangaId, chapter.SourceChapterId, ct);

        if (chapterId is null && mangaWasCached)
        {
            // A cached manga id that no longer has the chapter may name a different manga now.
            mangaId = await MangaIdAsync(connection, chapter.SourceSeriesId, fresh: true, ct);
            chapterId = await ChapterIdAsync(connection, mangaId, chapter.SourceChapterId, ct);
        }

        if (chapterId is null)
        {
            // Suwayomi's list can be older than Maki's; one refresh from the source, then give up.
            var (refreshedId, refresh) = await RefreshAsync(connection, chapter.SourceSeriesId, manga: false, chapters: true, ct);
            mangaId = refreshedId;
            foreach (var listed in refresh.Chapters!)
            {
                cache.SetChapter(connection.BaseUri, mangaId, listed.Url, listed.Id);
            }

            chapterId = refresh.Chapters!.FirstOrDefault(c => c.Url == chapter.SourceChapterId)?.Id;
        }

        if (chapterId is not { } id)
        {
            throw NotListed(chapter);
        }

        var (pages, pagesFor) = await client.FetchPagesAsync(id, ct);
        if (pagesFor is not null && pagesFor != chapter.SourceChapterId)
        {
            cache.ForgetChapter(connection.BaseUri, mangaId, chapter.SourceChapterId);
            if (await client.FindChapterIdAsync(mangaId, chapter.SourceChapterId, ct) is not { } current)
            {
                throw NotListed(chapter);
            }

            cache.SetChapter(connection.BaseUri, mangaId, chapter.SourceChapterId, current);
            (pages, pagesFor) = await client.FetchPagesAsync(current, ct);
            if (pagesFor is not null && pagesFor != chapter.SourceChapterId)
            {
                throw new InvalidOperationException($"Suwayomi returned the pages of '{pagesFor}' for '{chapter.SourceChapterId}'");
            }
        }

        return new ChapterPages([.. pages.Select(p => new PageRequest(connection.Resolve(p).ToString()))]);
    }

    /// <summary>
    /// Refreshes through the id resolved for <paramref name="url"/>, and when the manga that comes
    /// back is a different one (the cached id outlived a recreated Suwayomi database), resolves
    /// afresh and tries once more.
    /// </summary>
    private async Task<(int MangaId, SuwayomiRefresh Refresh)> RefreshAsync(
        SuwayomiConnection connection, string url, bool manga, bool chapters, CancellationToken ct)
    {
        var mangaId = await MangaIdAsync(connection, url, fresh: false, ct);
        var refresh = await client.RefreshAsync(mangaId, manga, chapters, ct);
        if (refresh.Manga is null || refresh.Manga.Url == url)
        {
            return (mangaId, refresh);
        }

        mangaId = await MangaIdAsync(connection, url, fresh: true, ct);
        refresh = await client.RefreshAsync(mangaId, manga, chapters, ct);
        return refresh.Manga is null || refresh.Manga.Url == url
            ? (mangaId, refresh)
            : throw new InvalidOperationException($"Suwayomi returned '{refresh.Manga.Url}' for '{url}'");
    }

    private async Task<int> MangaIdAsync(SuwayomiConnection connection, string url, bool fresh, CancellationToken ct)
    {
        if (fresh)
        {
            cache.ForgetManga(connection.BaseUri, SourceId, url);
        }
        else if (cache.Manga(connection.BaseUri, SourceId, url) is { } cached)
        {
            return cached;
        }

        var id = await client.FindMangaIdAsync(SourceId, url, ct)
            ?? throw new SourceErrorException(
                $"Suwayomi has no record of '{url}' on {DisplayName}. Search for the title once in Suwayomi so it is added again; the next sync finds it by its URL.");
        cache.SetManga(connection.BaseUri, SourceId, url, id);
        return id;
    }

    private async Task<int?> ChapterIdAsync(SuwayomiConnection connection, int mangaId, string url, CancellationToken ct)
    {
        if (cache.Chapter(connection.BaseUri, mangaId, url) is { } cached)
        {
            return cached;
        }

        var id = await client.FindChapterIdAsync(mangaId, url, ct);
        if (id is { } found)
        {
            cache.SetChapter(connection.BaseUri, mangaId, url, found);
        }

        return id;
    }

    private SourceChapter ToSourceChapter(SuwayomiChapter chapter, string seriesUrl)
    {
        var parsed = ChapterNumberParser.Parse(chapter.Name);
        return new SourceChapter(
            Name,
            seriesUrl,
            chapter.Url,
            chapter.Name,
            chapter.ChapterNumber >= 0 ? (decimal)chapter.ChapterNumber : null,
            parsed.Volume,
            TitleOf(chapter.Name),
            chapterLanguage,
            chapter.UploadDate > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(chapter.UploadDate).UtcDateTime : null,
            chapter.RealUrl,
            string.IsNullOrWhiteSpace(chapter.Scanlator) ? null : chapter.Scanlator.Trim());
    }

    /// <summary>The part of a Mihon chapter name after its number, "Vol.3 Ch.15 - The Fight" giving "The Fight".</summary>
    internal static string? TitleOf(string name)
    {
        var match = NamedChapter().Match(name);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    [GeneratedRegex(@"^\s*(?:vol(?:ume)?\.?\s*\d+\s*)?(?:ch(?:apter)?\.?\s*\d+(?:\.\d+)?)\s*[-:]\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex NamedChapter();

    private string? CoverUrl(SuwayomiManga manga) =>
        string.IsNullOrEmpty(manga.ThumbnailUrl) || manga.Id <= 0 ? null : ThumbnailPath(SourceId, manga.Id);

    /// <summary>The site's own page when Suwayomi knows it, else the manga's page in Suwayomi's web UI.</summary>
    private static string SeriesUrl(SuwayomiConnection connection, SuwayomiManga manga) =>
        !string.IsNullOrWhiteSpace(manga.RealUrl) ? manga.RealUrl
        : connection.Resolve($"/manga/{manga.Id.ToString(CultureInfo.InvariantCulture)}").ToString();

    private static HttpRequestException NotListed(SourceChapter chapter) =>
        new($"Suwayomi does not list chapter '{chapter.SourceChapterId}'", null, HttpStatusCode.NotFound);
}
