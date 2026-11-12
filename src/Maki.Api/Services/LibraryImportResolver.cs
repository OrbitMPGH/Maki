using System.Globalization;
using System.Text.RegularExpressions;
using Maki.Core.Metadata;
using Maki.Core.Scrobbling;
using Maki.Metadata.MangaBaka;

namespace Maki.Api.Services;

/// <summary>What a pasted id or link names: the service it belongs to and its id there.</summary>
public enum ImportIdService { MangaBaka, AniList, MyAnimeList }

/// <param name="Match">The resolved series, or null when <paramref name="ErrorKey"/> says why not.</param>
public record ImportIdResolution(MetadataSearchResult? Match, string? ErrorKey);

/// <summary>
/// Turns what somebody pastes into the import review (a MangaBaka id or link, or an AniList or
/// MyAnimeList link or id) into the MangaBaka series the import adopts the folder as. AniList and
/// MyAnimeList ids resolve through the local MangaBaka dump's cross-references, the same lookup
/// import lists use.
/// </summary>
public partial class LibraryImportResolver(
    IEnumerable<IMetadataProvider> metadataProviders, MangaBakaLocalStore store)
{
    [GeneratedRegex(@"^(mb|mangabaka|al|anilist|mal|myanimelist)\s*[:#]\s*(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex Prefixed();

    public static (ImportIdService Service, long Id)? Parse(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var bare))
        {
            return bare > 0 ? (ImportIdService.MangaBaka, bare) : null;
        }

        if (Prefixed().Match(text) is { Success: true } prefixed &&
            long.TryParse(prefixed.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) &&
            id > 0)
        {
            return (prefixed.Groups[1].Value.ToLowerInvariant() switch
            {
                "al" or "anilist" => ImportIdService.AniList,
                "mal" or "myanimelist" => ImportIdService.MyAnimeList,
                _ => ImportIdService.MangaBaka,
            }, id);
        }

        var links = ScrobbleMatching.ParseWebLinks([text]);
        foreach (var (key, service) in new[]
                 {
                     ("mangabaka", ImportIdService.MangaBaka),
                     ("anilist", ImportIdService.AniList),
                     ("mal", ImportIdService.MyAnimeList),
                 })
        {
            if (links.TryGetValue(key, out var raw) &&
                long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var linked) && linked > 0)
            {
                return (service, linked);
            }
        }

        return null;
    }

    /// <summary>
    /// The MangaBaka series a set of web links names, trying MangaBaka, AniList, MyAnimeList and
    /// MangaUpdates in that order, with the link that resolved. Null when none does or the dump is missing.
    /// </summary>
    public async Task<(MetadataSearchResult Match, string Link)?> ResolveLinksAsync(
        IReadOnlyList<string> links, CancellationToken ct)
    {
        if (links.Count == 0 || !await store.IsAvailableAsync(ct))
        {
            return null;
        }

        foreach (var link in links)
        {
            var found = ScrobbleMatching.ParseWebLinks([link]);
            long? mangaBakaId = null;
            if (found.TryGetValue("mangabaka", out var mb) &&
                long.TryParse(mb, NumberStyles.None, CultureInfo.InvariantCulture, out var mbId) && mbId > 0)
            {
                mangaBakaId = mbId;
            }
            else if (found.TryGetValue("anilist", out var al) &&
                     long.TryParse(al, NumberStyles.None, CultureInfo.InvariantCulture, out var alId))
            {
                mangaBakaId = await ByExternalAsync(MangaBakaLocalStore.ExternalSource.AniList, alId, ct);
            }
            else if (found.TryGetValue("mal", out var mal) &&
                     long.TryParse(mal, NumberStyles.None, CultureInfo.InvariantCulture, out var malId))
            {
                mangaBakaId = await ByExternalAsync(MangaBakaLocalStore.ExternalSource.MyAnimeList, malId, ct);
            }
            else if (found.TryGetValue("mangaupdates", out var mu) &&
                     (await store.GetIdsByMangaUpdatesIdsAsync([mu], ct)).TryGetValue(mu, out var muId))
            {
                mangaBakaId = muId;
            }

            if (mangaBakaId is { } id &&
                await metadataProviders.First().GetAsync(id.ToString(CultureInfo.InvariantCulture), ct) is { } metadata)
            {
                return (new MetadataSearchResult(metadata.ProviderId, metadata.Title, metadata.CoverUrl, metadata.Year,
                    metadata.Status, metadata.Description, metadata.TotalChapters), link);
            }
        }

        return null;
    }

    private async Task<long?> ByExternalAsync(MangaBakaLocalStore.ExternalSource source, long id, CancellationToken ct) =>
        (await store.GetIdsByExternalIdsAsync(source, [id], ct)).TryGetValue(id, out var mapped) ? mapped : null;

    public async Task<ImportIdResolution> ResolveAsync(string? input, CancellationToken ct)
    {
        if (Parse(input) is not var (service, id))
        {
            return new(null, "error.libraryImport.idUnrecognized");
        }

        if (!await store.IsAvailableAsync(ct))
        {
            return new(null, "error.libraryImport.idNeedsDump");
        }

        var mangaBakaId = id;
        if (service != ImportIdService.MangaBaka)
        {
            var source = service == ImportIdService.AniList
                ? MangaBakaLocalStore.ExternalSource.AniList
                : MangaBakaLocalStore.ExternalSource.MyAnimeList;
            var map = await store.GetIdsByExternalIdsAsync(source, [id], ct);
            if (!map.TryGetValue(id, out mangaBakaId))
            {
                return new(null, "error.libraryImport.idNotFound");
            }
        }

        var metadata = await metadataProviders.First()
            .GetAsync(mangaBakaId.ToString(CultureInfo.InvariantCulture), ct);
        return metadata is null
            ? new(null, "error.libraryImport.idNotFound")
            : new(new MetadataSearchResult(metadata.ProviderId, metadata.Title, metadata.CoverUrl, metadata.Year,
                metadata.Status, metadata.Description, metadata.TotalChapters), null);
    }
}
