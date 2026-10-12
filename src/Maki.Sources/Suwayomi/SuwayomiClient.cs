using System.Text.Json;
using Maki.Core.Sources;

namespace Maki.Sources.Suwayomi;

public sealed record SuwayomiAbout(string Name, string Version, string? BuildType);

public sealed record SuwayomiSourceInfo(
    string Id, string Name, string DisplayName, string Lang, string? ContentWarning, bool IsNsfw);

public sealed record SuwayomiManga(
    int Id,
    string? SourceId,
    string Url,
    string Title,
    string? ThumbnailUrl,
    string? Author,
    string? Description,
    string? RealUrl,
    string? Status);

/// <param name="ChapterNumber">Mihon's float, already rounded back to the value the extension set; negative means unknown.</param>
/// <param name="UploadDate">Epoch milliseconds, 0 when the extension gave none.</param>
public sealed record SuwayomiChapter(
    int Id,
    string Url,
    string Name,
    double ChapterNumber,
    string? Scanlator,
    long UploadDate,
    int SourceOrder,
    string? RealUrl);

/// <summary>
/// What one refresh of a manga returned. <see cref="Chapters"/> is null when not asked for;
/// <see cref="Manga"/> comes back either way, so a caller can check whose chapters these are.
/// </summary>
public sealed record SuwayomiRefresh(SuwayomiManga? Manga, IReadOnlyList<SuwayomiChapter>? Chapters);

/// <summary>
/// Typed calls against Suwayomi's GraphQL API, one instance for the whole app. Every query carries
/// an operation name. Two schema differences between v2.1 and v2.4 are handled by falling back on
/// the validation error rather than by parsing version strings: <c>fetchMangaAndChapters</c>
/// (v2.4+, else <c>fetchManga</c> + <c>fetchChapters</c>) and <c>contentWarning</c> on sources (v2.4+).
/// </summary>
public sealed class SuwayomiClient(IHttpClientFactory httpClientFactory, SuwayomiSettings settings, TimeProvider time)
{
    public const string HttpClientName = "suwayomi";
    public const string PagesHttpClientName = "suwayomi-pages";

    /// <summary>A source id missing from the cached set refetches it, but at most this often.</summary>
    private static readonly TimeSpan InstalledRefetchFloor = TimeSpan.FromMinutes(1);

    /// <summary>A present source id is trusted this long before the set is refetched anyway.</summary>
    private static readonly TimeSpan InstalledTtl = TimeSpan.FromMinutes(10);

    private const string MangaFields = "id sourceId url title thumbnailUrl author description realUrl status";
    private const string ChapterFields = "id url name chapterNumber scanlator uploadDate sourceOrder realUrl";

    private sealed record InstalledSet(Uri For, HashSet<string> Ids, DateTimeOffset FetchedAt);

    private readonly SemaphoreSlim _installedGate = new(1, 1);
    private volatile InstalledSet? _installed;
    private volatile bool _legacyRefresh;
    private volatile bool _legacySourceFields;

    public async Task<SuwayomiConnection> ConnectionAsync(CancellationToken ct = default) =>
        await settings.GetAsync(ct) ?? throw new SourceErrorException("Suwayomi is not configured in Maki's settings");

    /// <param name="anonymous">
    /// Send no credentials. <c>aboutServer</c> needs none under <c>ui_login</c>, so a version check
    /// can run before signing in, which a server older than v2.2 could not do at all. Under
    /// <c>basic_auth</c> every request needs the header, so this is for <c>ui_login</c> only.
    /// </param>
    public async Task<SuwayomiAbout> AboutAsync(CancellationToken ct = default, bool anonymous = false)
    {
        var data = await PostAsync("AboutServer", "query AboutServer { aboutServer { name version buildType } }", null, ct, anonymous);
        var about = data.GetProperty("aboutServer");
        return new SuwayomiAbout(
            SuwayomiGraphQl.String(about, "name") ?? "Suwayomi",
            SuwayomiGraphQl.String(about, "version") ?? "",
            SuwayomiGraphQl.String(about, "buildType"));
    }

    /// <param name="probe">
    /// Skip the queue every other API call waits in. For the health check, whose short timeout would
    /// otherwise expire behind a busy sync and report a healthy server as down.
    /// </param>
    public async Task<IReadOnlyList<SuwayomiSourceInfo>> ListSourcesAsync(CancellationToken ct = default, bool probe = false)
    {
        JsonElement data;
        if (!_legacySourceFields)
        {
            try
            {
                data = await PostAsync("Sources",
                    "query Sources { sources { nodes { id name lang displayName isNsfw contentWarning } } }", null, ct,
                    skipLimit: probe);
                return ParseSources(data);
            }
            catch (SuwayomiGraphQlException ex) when (ex.IsUndefinedField("contentWarning"))
            {
                _legacySourceFields = true;
            }
        }

        data = await PostAsync("Sources", "query Sources { sources { nodes { id name lang displayName isNsfw } } }", null, ct,
            skipLimit: probe);
        return ParseSources(data);
    }

    /// <summary>
    /// Throws a <see cref="SourceErrorException"/> when the extension behind <paramref name="sourceId"/>
    /// is no longer installed. Without this, Suwayomi fails such a call with an error carrying no message.
    /// </summary>
    public async Task EnsureInstalledAsync(string sourceId, CancellationToken ct = default)
    {
        var connection = await ConnectionAsync(ct);
        var now = time.GetUtcNow();
        var known = _installed;
        if (known is not null && known.For == connection.BaseUri)
        {
            var age = now - known.FetchedAt;
            if (known.Ids.Contains(sourceId) ? age < InstalledTtl : age < InstalledRefetchFloor)
            {
                if (known.Ids.Contains(sourceId))
                {
                    return;
                }

                throw NotInstalled(sourceId);
            }
        }

        await _installedGate.WaitAsync(ct);
        try
        {
            // Another caller may have refetched while this one waited.
            known = _installed;
            if (known is null || known.For != connection.BaseUri || now - known.FetchedAt >= InstalledRefetchFloor)
            {
                var data = await PostAsync("InstalledSources", "query InstalledSources { sources { nodes { id } } }", null, ct);
                var ids = Nodes(data, "sources")
                    .Select(n => SuwayomiGraphQl.LongString(n, "id"))
                    .OfType<string>()
                    .ToHashSet(StringComparer.Ordinal);
                known = new InstalledSet(connection.BaseUri, ids, time.GetUtcNow());
                _installed = known;
            }
        }
        finally
        {
            _installedGate.Release();
        }

        if (!known.Ids.Contains(sourceId))
        {
            throw NotInstalled(sourceId);
        }
    }

    public async Task<IReadOnlyList<SuwayomiManga>> SearchAsync(string sourceId, string query, CancellationToken ct = default)
    {
        var data = await PostAsync("Search",
            "mutation Search($source: LongString!, $query: String!) { fetchSourceManga(input: {source: $source, type: SEARCH, page: 1, query: $query}) { mangas { " + MangaFields + " } } }",
            new { source = sourceId, query }, ct);
        var payload = data.GetProperty("fetchSourceManga");
        return payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("mangas", out var mangas) &&
               mangas.ValueKind == JsonValueKind.Array
            ? [.. mangas.EnumerateArray().Select(ParseManga)]
            : throw new InvalidOperationException("Suwayomi answered Search without a manga list");
    }

    /// <summary>The local id of a manga Suwayomi already has, or null when this instance has never seen it.</summary>
    public async Task<int?> FindMangaIdAsync(string sourceId, string url, CancellationToken ct = default)
    {
        var data = await PostAsync("MangaByUrl",
            "query MangaByUrl($sourceId: LongString!, $url: String!) { mangas(condition: {sourceId: $sourceId, url: $url}) { nodes { id } } }",
            new { sourceId, url }, ct);
        return Nodes(data, "mangas").Select(n => SuwayomiGraphQl.Int(n, "id")).Cast<int?>().FirstOrDefault(id => id > 0);
    }

    /// <summary>The source a local manga id belongs to, or null when there is no such manga.</summary>
    public async Task<string?> MangaSourceIdAsync(int mangaId, CancellationToken ct = default)
    {
        var data = await PostAsync("MangaSource",
            "query MangaSource($id: Int!) { manga(id: $id) { sourceId } }", new { id = mangaId }, ct);
        return data.TryGetProperty("manga", out var manga) && manga.ValueKind == JsonValueKind.Object
            ? SuwayomiGraphQl.LongString(manga, "sourceId")
            : null;
    }

    /// <summary>Asks Suwayomi to refresh a manga and/or its chapter list from the source and returns the result.</summary>
    public async Task<SuwayomiRefresh> RefreshAsync(int mangaId, bool manga, bool chapters, CancellationToken ct = default)
    {
        if (!_legacyRefresh)
        {
            try
            {
                var data = await PostAsync("Refresh",
                    "mutation Refresh($id: Int!, $fetchManga: Boolean!, $fetchChapters: Boolean!) { fetchMangaAndChapters(input: {id: $id, fetchManga: $fetchManga, fetchChapters: $fetchChapters}) { manga { " + MangaFields + " } chapters { " + ChapterFields + " } } }",
                    new { id = mangaId, fetchManga = manga, fetchChapters = chapters }, ct);
                var payload = data.GetProperty("fetchMangaAndChapters");
                // The manga comes back even when only chapters were refreshed, and its url is what
                // tells a caller a cached id now names some other manga.
                return new SuwayomiRefresh(
                    payload.TryGetProperty("manga", out var m) && m.ValueKind == JsonValueKind.Object ? ParseManga(m)
                    : manga ? throw new InvalidOperationException("Suwayomi answered Refresh without the manga")
                    : null,
                    chapters ? ParseChapters(payload) : null);
            }
            catch (SuwayomiGraphQlException ex) when (ex.IsUndefinedField("fetchMangaAndChapters"))
            {
                _legacyRefresh = true;
            }
        }

        SuwayomiManga? refreshedManga;
        if (manga)
        {
            var data = await PostAsync("RefreshManga",
                "mutation RefreshManga($id: Int!) { fetchManga(input: {id: $id}) { manga { " + MangaFields + " } } }",
                new { id = mangaId }, ct);
            refreshedManga = ParseManga(data.GetProperty("fetchManga").GetProperty("manga"));
        }
        else
        {
            // Read from Suwayomi's database without touching the source. fetchChapters says nothing
            // about which manga it listed, and without the url the caller cannot tell that a cached
            // id outlived a recreated database and now names some other manga.
            var data = await PostAsync("MangaById",
                "query MangaById($id: Int!) { manga(id: $id) { " + MangaFields + " } }", new { id = mangaId }, ct);
            refreshedManga = data.TryGetProperty("manga", out var stored) && stored.ValueKind == JsonValueKind.Object
                ? ParseManga(stored)
                : null;
        }

        IReadOnlyList<SuwayomiChapter>? refreshedChapters = null;
        if (chapters)
        {
            var data = await PostAsync("RefreshChapters",
                "mutation RefreshChapters($id: Int!) { fetchChapters(input: {mangaId: $id}) { chapters { " + ChapterFields + " } } }",
                new { id = mangaId }, ct);
            refreshedChapters = ParseChapters(data.GetProperty("fetchChapters"));
        }

        return new SuwayomiRefresh(refreshedManga, refreshedChapters);
    }

    public async Task<int?> FindChapterIdAsync(int mangaId, string url, CancellationToken ct = default)
    {
        var data = await PostAsync("ChapterByUrl",
            "query ChapterByUrl($mangaId: Int!, $url: String!) { chapters(condition: {mangaId: $mangaId, url: $url}) { nodes { id } } }",
            new { mangaId, url }, ct);
        return Nodes(data, "chapters").Select(n => SuwayomiGraphQl.Int(n, "id")).Cast<int?>().FirstOrDefault(id => id > 0);
    }

    /// <summary>The chapter's page paths, server-relative as Suwayomi returns them, and the url of the chapter they belong to.</summary>
    public async Task<(IReadOnlyList<string> Pages, string? ChapterUrl)> FetchPagesAsync(int chapterId, CancellationToken ct = default)
    {
        var data = await PostAsync("Pages",
            "mutation Pages($chapterId: Int!) { fetchChapterPages(input: {chapterId: $chapterId}) { pages chapter { url } } }",
            new { chapterId }, ct);
        var payload = data.GetProperty("fetchChapterPages");
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("pages", out var pages) ||
            pages.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Suwayomi answered Pages without a page list");
        }

        var chapterUrl = payload.TryGetProperty("chapter", out var chapter) && chapter.ValueKind == JsonValueKind.Object
            ? SuwayomiGraphQl.String(chapter, "url")
            : null;
        return ([.. pages.EnumerateArray().Select(p => p.GetString()).OfType<string>()], chapterUrl);
    }

    private async Task<JsonElement> PostAsync(
        string operation, string query, object? variables, CancellationToken ct, bool skipAuth = false, bool skipLimit = false)
    {
        var connection = await ConnectionAsync(ct);
        return await SuwayomiGraphQl.PostAsync(
            httpClientFactory.CreateClient(HttpClientName), connection.GraphQlUri, operation, query, variables, ct,
            skipAuth, skipLimit);
    }

    private static SourceErrorException NotInstalled(string sourceId) =>
        new($"The Suwayomi extension for source {sourceId} is no longer installed. Reinstall it in Suwayomi, or remove the source in Maki's Suwayomi settings.");

    private static IEnumerable<JsonElement> Nodes(JsonElement data, string field) =>
        data.TryGetProperty(field, out var connection) && connection.ValueKind == JsonValueKind.Object &&
        connection.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array
            ? nodes.EnumerateArray()
            : throw new InvalidOperationException($"Suwayomi answered without a {field} list");

    private static List<SuwayomiSourceInfo> ParseSources(JsonElement data) =>
        [.. Nodes(data, "sources").Select(n => new SuwayomiSourceInfo(
            SuwayomiGraphQl.LongString(n, "id") ?? "",
            SuwayomiGraphQl.String(n, "name") ?? "",
            SuwayomiGraphQl.String(n, "displayName") ?? SuwayomiGraphQl.String(n, "name") ?? "",
            SuwayomiGraphQl.String(n, "lang") ?? "",
            SuwayomiGraphQl.String(n, "contentWarning"),
            n.TryGetProperty("isNsfw", out var nsfw) && nsfw.ValueKind == JsonValueKind.True))
            .Where(s => s.Id.Length > 0)];

    private static SuwayomiManga ParseManga(JsonElement m) =>
        m.ValueKind != JsonValueKind.Object
            ? throw new InvalidOperationException("Suwayomi answered without the manga")
            : new SuwayomiManga(
                SuwayomiGraphQl.Int(m, "id"),
                SuwayomiGraphQl.LongString(m, "sourceId"),
                SuwayomiGraphQl.String(m, "url") ?? "",
                SuwayomiGraphQl.String(m, "title") ?? "",
                SuwayomiGraphQl.String(m, "thumbnailUrl"),
                SuwayomiGraphQl.String(m, "author"),
                SuwayomiGraphQl.String(m, "description"),
                SuwayomiGraphQl.String(m, "realUrl"),
                SuwayomiGraphQl.String(m, "status"));

    private static List<SuwayomiChapter> ParseChapters(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("chapters", out var chapters) &&
        chapters.ValueKind == JsonValueKind.Array
            ? [.. chapters.EnumerateArray().Select(c => new SuwayomiChapter(
                SuwayomiGraphQl.Int(c, "id"),
                SuwayomiGraphQl.String(c, "url") ?? "",
                SuwayomiGraphQl.String(c, "name") ?? "",
                ChapterNumber(c),
                SuwayomiGraphQl.String(c, "scanlator"),
                SuwayomiGraphQl.Long(c, "uploadDate"),
                SuwayomiGraphQl.Int(c, "sourceOrder"),
                SuwayomiGraphQl.String(c, "realUrl")))]
            : throw new InvalidOperationException("Suwayomi answered without a chapter list");

    /// <summary>
    /// Mihon stores the number as a 32-bit float and Suwayomi widens it, so 10.1 arrives as
    /// 10.100000381469727. Narrowing back to float and printing its shortest form recovers 10.1.
    /// </summary>
    private static double ChapterNumber(JsonElement chapter) =>
        chapter.TryGetProperty("chapterNumber", out var n) && n.ValueKind == JsonValueKind.Number
            ? double.Parse(((float)n.GetDouble()).ToString(System.Globalization.CultureInfo.InvariantCulture),
                System.Globalization.CultureInfo.InvariantCulture)
            : -1;
}
