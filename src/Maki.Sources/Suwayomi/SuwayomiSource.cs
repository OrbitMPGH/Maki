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
    SuwayomiStoredSource stored,
    Uri baseUri,
    SuwayomiClient client,
    SuwayomiMangaIdCache cache) : ISource, IPageClientSource
{
    public const string NamePrefix = "suwayomi-";

    /// <summary>The Mihon source id, as Suwayomi sends it.</summary>
    public string SourceId { get; } = stored.Id;

    public string Name { get; } = NamePrefix + stored.Id;

    public string DisplayName { get; } =
        string.IsNullOrWhiteSpace(stored.DisplayName) ? stored.Name : stored.DisplayName;

    /// <summary>An extension that cannot read a date falls back to the fetch time, so a whole list can share one.</summary>
    public bool ReleaseDatesAreReliable => false;

    /// <summary>
    /// The site without its language, "MangaDex" for "MangaDex (EN)". Mihon extensions register one
    /// source per language, so this is what ties a site's language variants together.
    /// </summary>
    public string SiteName { get; } = string.IsNullOrWhiteSpace(stored.Name) ? stored.DisplayName : stored.Name;

    /// <summary>The extension's own language code, <c>all</c>/<c>multi</c>/<c>other</c> for one that mixes them.</summary>
    public string Lang { get; } = stored.Lang;

    public bool MixedLanguage { get; } = SuwayomiStoredSource.IsMixedLanguage(stored.Lang);

    /// <summary>
    /// What every chapter is filed under. Null for a mixed-language source nobody picked a language
    /// for yet, which stays switched off until someone does (<c>SourceAvailability</c>).
    /// </summary>
    public string? ChapterLanguage { get; } = stored.EffectiveChapterLanguage;

    public bool NeedsSetup => ChapterLanguage is null;

    /// <summary>Suwayomi no longer listed it at the last refresh.</summary>
    public bool Missing { get; } = stored.Missing;

    public string BaseUrl { get; } = baseUri.GetLeftPart(UriPartial.Authority) + baseUri.AbsolutePath.TrimEnd('/');

    public SourceCapabilities Capabilities => SourceCapabilities.None;

    public SourceRating Rating { get; } = stored.ContentWarning?.ToUpperInvariant() switch
    {
        "NSFW" => SourceRating.Adult,
        "MIXED" => SourceRating.Mature,
        null when stored.IsNsfw => SourceRating.Adult,
        _ => SourceRating.General
    };

    public IReadOnlyList<string> SupportedLanguages { get; } =
        stored.EffectiveChapterLanguage is { } language ? [language] : [];

    public string PageHttpClientName => SuwayomiClient.PagesHttpClientName;

    public bool IsPageUrlAllowed(Uri url) =>
        url.IsAbsoluteUri &&
        string.Equals(url.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(url.IdnHost, baseUri.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        url.Port == baseUri.Port;

    /// <summary>The Maki endpoint that relays a Suwayomi thumbnail, since covers on a LAN host can't go through the cover proxy.</summary>
    public static string ThumbnailPath(string sourceId, int mangaId) =>
        $"{ThumbnailPrefix}{sourceId}/{mangaId.ToString(CultureInfo.InvariantCulture)}";

    public const string ThumbnailPrefix = "/api/v1/suwayomi/thumbnail/";

    /// <summary>Exactly <c>/api/v1/suwayomi/thumbnail/{sourceId}/{mangaId}</c>, nothing longer or looser.</summary>
    public static bool IsThumbnailPath(string url) => ThumbnailPattern().IsMatch(url);

    [GeneratedRegex(@"^/api/v1/suwayomi/thumbnail/\d{1,20}/\d{1,10}$")]
    private static partial Regex ThumbnailPattern();

    public async Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        await client.EnsureInstalledAsync(SourceId, ct);
        var connection = await client.ConnectionAsync(ct);
        var results = await client.SearchAsync(SourceId, title, ct);
        foreach (var manga in results.Where(m => m.Id > 0))
        {
            cache.SetOwner(connection.BaseUri, manga.Id, SourceId);
        }

        return [.. results
            .Where(m => m.Url.Length > 0)
            .Select(m => new SourceSeriesResult(
                m.Url, m.Title, SeriesUrl(m), CoverUrl(m), m.Description, Author: m.Author))];
    }

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        await client.EnsureInstalledAsync(SourceId, ct);
        var connection = await client.ConnectionAsync(ct);
        var (_, refresh) = await RefreshAsync(connection, sourceSeriesId, manga: true, chapters: false, ct);
        var manga = refresh.Manga!;
        cache.SetOwner(connection.BaseUri, manga.Id, SourceId);
        return new SourceSeriesDetail(
            sourceSeriesId, manga.Title, SeriesUrl(manga), CoverUrl(manga), manga.Description,
            manga.Status?.ToLowerInvariant());
    }

    /// <param name="languageFilter">Ignored: a Mihon source publishes one language, or the one the admin assigned it.</param>
    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        // Filing a mixed-language source's chapters under a guessed language would put them in the
        // wrong series folder and write the wrong LanguageISO, so it waits for a choice.
        var language = ChapterLanguage ?? throw new SourceErrorException(
            $"{DisplayName} publishes several languages. Choose the one to file its chapters under in Manage sources.");
        await client.EnsureInstalledAsync(SourceId, ct);
        var connection = await client.ConnectionAsync(ct);
        var (mangaId, refresh) = await RefreshAsync(connection, sourceSeriesId, manga: false, chapters: true, ct);
        var chapters = refresh.Chapters!
            .Where(c => c.Url.Length > 0 && ChapterAccessReader.Of(c.Name) == ChapterAccess.Free)
            .ToList();
        foreach (var chapter in chapters)
        {
            cache.SetChapter(connection.BaseUri, mangaId, chapter.Url, chapter.Id);
        }

        // Extensions such as MangaDex list one entry per scanlation group; the newest upload wins,
        // then the earliest in the source's own order, rather than whichever the extension sorted first.
        return SourceChapterList.Normalize(
            chapters,
            c => ToSourceChapter(c, sourceSeriesId, language),
            group => group.OrderByDescending(c => c.UploadDate).ThenBy(c => c.SourceOrder).First());
    }

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        await client.EnsureInstalledAsync(SourceId, ct);
        var connection = await client.ConnectionAsync(ct);
        var mangaWasCached = cache.Manga(connection.BaseUri, SourceId, chapter.SourceSeriesId) is not null;
        var mangaId = await MangaIdAsync(connection, chapter.SourceSeriesId, fresh: false, ct);
        var cachedChapterId = cache.Chapter(connection.BaseUri, mangaId, chapter.SourceChapterId);
        var chapterId = await ChapterIdAsync(connection, mangaId, chapter.SourceChapterId, ct);

        if (chapterId is null && mangaWasCached)
        {
            // A cached manga id that no longer has the chapter may name a different manga now.
            mangaId = await MangaIdAsync(connection, chapter.SourceSeriesId, fresh: true, ct);
            cachedChapterId = null;
            chapterId = await ChapterIdAsync(connection, mangaId, chapter.SourceChapterId, ct);
        }

        if (chapterId is null)
        {
            // Suwayomi's list can be older than Maki's; one refresh from the source, then give up.
            var (refreshedId, refresh) = await RefreshAsync(connection, chapter.SourceSeriesId, manga: false, chapters: true, ct);
            mangaId = refreshedId;
            cachedChapterId = null;
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

        IReadOnlyList<string> pages;
        string? pagesFor;
        try
        {
            (pages, pagesFor) = await client.FetchPagesAsync(id, ct);
        }
        catch (SuwayomiGraphQlException) when (cachedChapterId == id)
        {
            // A cached chapter id that Suwayomi no longer has fails the call. Look it up again; if it
            // is the same id the failure is the site's own, so it is not asked a second time.
            var current = await ReResolveChapterAsync();
            if (current == id)
            {
                throw;
            }

            id = current;
            (pages, pagesFor) = await client.FetchPagesAsync(id, ct);
        }

        if (pagesFor is not null && pagesFor != chapter.SourceChapterId)
        {
            id = await ReResolveChapterAsync();
            (pages, pagesFor) = await client.FetchPagesAsync(id, ct);
            if (pagesFor is not null && pagesFor != chapter.SourceChapterId)
            {
                throw new InvalidOperationException($"Suwayomi returned the pages of '{pagesFor}' for '{chapter.SourceChapterId}'");
            }
        }

        async Task<int> ReResolveChapterAsync()
        {
            cache.ForgetChapter(connection.BaseUri, mangaId, chapter.SourceChapterId);
            if (await client.FindChapterIdAsync(mangaId, chapter.SourceChapterId, ct) is not { } current)
            {
                throw NotListed(chapter);
            }

            cache.SetChapter(connection.BaseUri, mangaId, chapter.SourceChapterId, current);
            return current;
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
        var cachedId = cache.Manga(connection.BaseUri, SourceId, url);
        var mangaId = await MangaIdAsync(connection, url, fresh: false, ct);
        SuwayomiRefresh refresh;
        try
        {
            refresh = await client.RefreshAsync(mangaId, manga, chapters, ct);
        }
        catch (SuwayomiGraphQlException) when (cachedId == mangaId)
        {
            // A cached id Suwayomi no longer has fails the call. Resolve the url again; the same id
            // means the failure is the site's own, so it is not asked a second time.
            var resolved = await MangaIdAsync(connection, url, fresh: true, ct);
            if (resolved == mangaId)
            {
                throw;
            }

            mangaId = resolved;
            refresh = await client.RefreshAsync(mangaId, manga, chapters, ct);
        }

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

    private SourceChapter ToSourceChapter(SuwayomiChapter chapter, string seriesUrl, string language)
    {
        var parsed = ChapterNumberParser.Parse(chapter.Name);
        return new SourceChapter(
            Name,
            seriesUrl,
            chapter.Url,
            chapter.Name,
            chapter.ChapterNumber >= 0 ? (decimal)chapter.ChapterNumber : parsed.NumberWithPart,
            parsed.Volume,
            TitleOf(chapter.Name),
            language,
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

    /// <summary>
    /// The site's own page when Suwayomi knows it, else empty. Suwayomi's web UI address is never
    /// offered instead: these URLs reach every user, and the admin's Suwayomi host is not theirs to see.
    /// </summary>
    private static string SeriesUrl(SuwayomiManga manga) =>
        !string.IsNullOrWhiteSpace(manga.RealUrl) ? manga.RealUrl : string.Empty;

    private static HttpRequestException NotListed(SourceChapter chapter) =>
        new($"Suwayomi does not list chapter '{chapter.SourceChapterId}'", null, HttpStatusCode.NotFound);
}
