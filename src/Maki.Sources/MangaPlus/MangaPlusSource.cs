using System.Text;
using Maki.Core.Parsing;
using Maki.Core.Sources;

namespace Maki.Sources.MangaPlus;

/// <summary>
/// MANGA Plus by Shueisha — the official free same-day English source for Shonen
/// Jump titles (One Piece, Kagurabachi, Dandadan, Jujutsu Kaisen…).
///
/// Uses the same web API the site's own SPA calls. Responses are protobuf: the old
/// <c>?format=json</c> shortcut is now rejected at the edge with an nginx 403, and sending it
/// is what made every call fail. Requests need a <c>SESSION-TOKEN</c> header (any value, the
/// site generates a UUID client-side and keeps it in localStorage) — without it the API answers
/// 200 with an "Account Banned" error popup instead of data. See <see cref="PbMessage"/> for
/// the decoder and the field numbers below for the shape of each response.
///
/// Page images are XOR-encrypted with a per-image hex key returned beside each page; the key
/// rides on the PageRequest and PageDownloader decrypts the fetched bytes.
///
/// Only the first- and latest-few chapters of each title are free — older chapters error
/// at the viewer endpoint (the grab then fails, which is expected: this source exists for
/// brand-new chapters). The API bans datacenter IPs, so it only works from a residential
/// IP (e.g. a home server), not most cloud hosts. There is no search endpoint, so we filter
/// the (cached) full catalog by normalized title.
/// </summary>
public class MangaPlusSource(IHttpClientFactory httpClientFactory) : ISource
{
    public const string HttpClientName = "source-mangaplus";

    // Response envelope.
    private const int ResponseSuccess = 1;
    private const int ResponseError = 2;

    // ErrorResult.englishPopup, and Popup.subject inside it.
    private const int ErrorEnglishPopup = 2;
    private const int PopupSubject = 1;

    // SuccessResult.allTitlesViewV2 → AllTitlesViewV2.AllTitlesGroup → AllTitlesGroup.titles.
    private const int SuccessAllTitlesView = 25;
    private const int AllTitlesGroups = 1;
    private const int GroupTitles = 2;

    // Title.
    private const int TitleId = 1;
    private const int TitleName = 2;
    private const int TitlePortraitImageUrl = 4;
    private const int TitleLanguage = 7;

    // SuccessResult.titleDetailView.
    private const int SuccessTitleDetailView = 8;
    private const int DetailTitle = 1;
    private const int DetailOverview = 3;
    private const int DetailChapterListGroups = 28;

    // ChapterListGroup splits its chapters across three lists (first / mid / last), the middle
    // one being the paywalled stretch the site shows as locked.
    private static readonly int[] ChapterLists = [2, 3, 4];

    // Chapter.
    private const int ChapterId = 2;
    private const int ChapterName = 3;
    private const int ChapterSubTitle = 4;

    // SuccessResult.mangaViewer → MangaViewer.pages → Page.mangaPage.
    private const int SuccessMangaViewer = 10;
    private const int ViewerPages = 1;
    private const int PageMangaPage = 1;
    private const int MangaPageImageUrl = 1;
    private const int MangaPageEncryptionKey = 5;

    private readonly SourceCatalog _catalog = new(TimeSpan.FromHours(1));

    public string Name => "mangaplus";
    public string DisplayName => "MANGA Plus";
    public string BaseUrl => "https://mangaplus.shueisha.co.jp";
    /// <summary>
    /// Deliberately not <see cref="SourceCapabilities.SupportsLanguageFilter"/> even though this
    /// source is multi-language: a MANGA Plus title id <em>is</em> a language — the Spanish One
    /// Piece is a different id with a different chapter list, not a filter over the English one —
    /// so there is nothing for a per-mapping filter to select. A second language is a second
    /// mapping, and <see cref="ListChaptersAsync"/> reports whichever language the mapped title is
    /// actually in.
    /// </summary>
    public SourceCapabilities Capabilities => SourceCapabilities.None;
    public IReadOnlyList<string> SupportedLanguages => MangaPlusLanguages.Codes;

    /// <summary>Shueisha serves the protobuf API and every image from its own CDN domain, not from the site.</summary>
    public IReadOnlyList<string> CoverHosts => ["tokyo-cdn.com"];

    private HttpClient Client => httpClientFactory.CreateClient(HttpClientName);

    public string? ResolveSeriesIdFromUrl(Uri url) =>
        // https://mangaplus.shueisha.co.jp/titles/{id}
        SourceUrl.PathTail(url, BaseUrl, "/titles/", firstSegmentOnly: true);

    public Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default) =>
        _catalog.SearchAsync(title, FetchCatalogAsync, ct);

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        var data = await GetAsync("title_detailV3", ct, ("title_id", sourceSeriesId));
        var view = data?.Message(SuccessTitleDetailView);
        var title = view?.Message(DetailTitle);

        return new SourceSeriesDetail(
            sourceSeriesId,
            title?.String(TitleName) ?? sourceSeriesId,
            $"{BaseUrl}/titles/{sourceSeriesId}",
            title?.String(TitlePortraitImageUrl),
            WithLanguage(view?.String(DetailOverview), MangaPlusLanguages.Code(title?.Number(TitleLanguage))));
    }

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        var data = await GetAsync("title_detailV3", ct, ("title_id", sourceSeriesId));
        var view = data?.Message(SuccessTitleDetailView);
        if (view is null)
        {
            return [];
        }

        // Read off the title itself rather than the mapping's filter. The id already decides the
        // language, so a filter could only ever contradict it — and before this was read, every
        // chapter of the Spanish or Thai catalog was written into the CBZ as English.
        var language = MangaPlusLanguages.Code(view.Message(DetailTitle)?.Number(TitleLanguage))
            ?? SourceLanguages.Default;

        var chapters = new List<SourceChapter>();
        foreach (var group in view.Messages(DetailChapterListGroups))
        {
            foreach (var list in ChapterLists)
            {
                foreach (var ch in group.Messages(list))
                {
                    var chapterId = ch.Number(ChapterId)?.ToString();
                    if (string.IsNullOrEmpty(chapterId))
                    {
                        continue;
                    }

                    // name is like "#12"; "ex" / one-shots have no number
                    var raw = ch.String(ChapterName)?.TrimStart('#');
                    var parsed = ChapterNumberParser.Parse(raw);
                    var subTitle = ch.String(ChapterSubTitle);

                    chapters.Add(new SourceChapter(
                        Name,
                        sourceSeriesId,
                        chapterId,
                        raw,
                        parsed.Number,
                        parsed.Volume,
                        Title: string.IsNullOrWhiteSpace(subTitle) ? null : subTitle,
                        Language: language,
                        ReleaseDate: null,
                        Url: $"{BaseUrl}/viewer/{chapterId}"));
                }
            }
        }

        return SourceChapterList.Normalize(chapters);
    }

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        var data = await GetAsync(
            "manga_viewer", ct,
            ("chapter_id", chapter.SourceChapterId),
            ("split", "yes"),
            ("img_quality", "high"));

        var pages = new List<PageRequest>();
        var viewer = data?.Message(SuccessMangaViewer);
        if (viewer is not null)
        {
            foreach (var page in viewer.Messages(ViewerPages))
            {
                // banners / ads have no mangaPage
                var mangaPage = page.Message(PageMangaPage);
                var imageUrl = mangaPage?.String(MangaPageImageUrl);
                if (string.IsNullOrEmpty(imageUrl))
                {
                    continue;
                }

                pages.Add(new PageRequest(imageUrl, Headers: null, XorKeyHex: mangaPage!.String(MangaPageEncryptionKey)));
            }
        }

        return new ChapterPages(pages);
    }

    private async Task<List<SourceSeriesResult>> FetchCatalogAsync(CancellationToken ct)
    {
        var data = await GetAsync("title_list/allV2", ct);
        var catalog = new List<SourceSeriesResult>();
        var seen = new HashSet<string>();

        var view = data?.Message(SuccessAllTitlesView);
        if (view is null)
        {
            return catalog;
        }

        // Non-English entries used to be dropped here, which made the other eight catalogs
        // unreachable: their titles are separate ids, so nothing else could ever find them.
        var translated = new List<SourceSeriesResult>();

        foreach (var group in view.Messages(AllTitlesGroups))
        {
            foreach (var title in group.Messages(GroupTitles))
            {
                var language = MangaPlusLanguages.Code(title.Number(TitleLanguage));
                if (language is null)
                {
                    // A language id this build doesn't know. Listing it would offer a series whose
                    // chapters could only be filed under a guessed code.
                    continue;
                }

                var id = title.Number(TitleId)?.ToString();
                var name = title.String(TitleName);
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name) || !seen.Add(id))
                {
                    continue;
                }

                var entry = new SourceSeriesResult(
                    id, name, $"{BaseUrl}/titles/{id}", title.String(TitlePortraitImageUrl),
                    WithLanguage(null, language));

                (language == SourceLanguages.Default ? catalog : translated).Add(entry);
            }
        }

        // English first, and SourceCatalog's ranking sort is stable — so the English One Piece still
        // outranks the Spanish one at an identical title score, and auto-matching keeps picking the
        // entry it picked before every other language became visible.
        catalog.AddRange(translated);
        return catalog;
    }

    /// <summary>
    /// Prefixes a description with the language its title is published in. Two entries for the same
    /// work carry the same name — the picker needs something on the card to tell them apart.
    /// </summary>
    private static string? WithLanguage(string? description, string? language)
    {
        if (language is null)
        {
            return description;
        }

        var label = $"[{MangaPlusLanguages.Name(language)}]";
        return string.IsNullOrWhiteSpace(description) ? label : $"{label} {description}";
    }

    /// <summary>
    /// GETs an API path and returns the decoded "success" payload (throwing the site's error
    /// popup text on failure). Do not add <c>format=json</c> here: the edge 403s any request
    /// carrying it.
    /// </summary>
    private async Task<PbMessage?> GetAsync(string path, CancellationToken ct, params (string Key, string Value)[] parameters)
    {
        var query = new StringBuilder();
        foreach (var (key, value) in parameters)
        {
            query.Append(query.Length == 0 ? '?' : '&').Append(key).Append('=').Append(Uri.EscapeDataString(value));
        }

        var body = await Client.GetByteArrayAsync(path + query, ct);
        var root = PbMessage.Parse(body);

        if (root.Message(ResponseError) is { } error)
        {
            throw new InvalidOperationException(
                error.Message(ErrorEnglishPopup)?.String(PopupSubject) ?? "MangaPlus API error");
        }

        return root.Message(ResponseSuccess);
    }
}
