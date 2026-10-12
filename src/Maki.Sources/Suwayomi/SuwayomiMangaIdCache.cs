using System.Collections.Concurrent;

namespace Maki.Sources.Suwayomi;

/// <summary>
/// Maps the portable keys Maki stores (a manga's and a chapter's source-relative <c>url</c>) to the
/// local ids one Suwayomi instance gave them. Keyed by the instance's base URL, so pointing Maki at
/// another server never reuses an id from the old one. An id can still go stale when the same
/// server's database is recreated, which is why callers check the url that comes back with it.
/// </summary>
public sealed class SuwayomiMangaIdCache
{
    /// <summary>
    /// Per map. Past it the map is emptied rather than trimmed: every entry can be looked up again in
    /// one cheap query, so a crude bound costs a few lookups and keeps a long-running instance from
    /// holding every chapter it ever listed.
    /// </summary>
    internal const int MaxEntries = 20_000;

    private readonly ConcurrentDictionary<(Uri Server, string SourceId, string Url), int> _manga = new();
    private readonly ConcurrentDictionary<(Uri Server, int MangaId, string Url), int> _chapters = new();
    private readonly ConcurrentDictionary<(Uri Server, int MangaId), string> _owners = new();

    public int? Manga(Uri server, string sourceId, string url) =>
        _manga.TryGetValue((server, sourceId, url), out var id) ? id : null;

    public void SetManga(Uri server, string sourceId, string url, int id) => Bounded(_manga)[(server, sourceId, url)] = id;

    public void ForgetManga(Uri server, string sourceId, string url) => _manga.TryRemove((server, sourceId, url), out _);

    public int? Chapter(Uri server, int mangaId, string url) =>
        _chapters.TryGetValue((server, mangaId, url), out var id) ? id : null;

    public void SetChapter(Uri server, int mangaId, string url, int id) => Bounded(_chapters)[(server, mangaId, url)] = id;

    public void ForgetChapter(Uri server, int mangaId, string url) => _chapters.TryRemove((server, mangaId, url), out _);

    /// <summary>Which source a local manga id belongs to, for the thumbnail endpoint's ownership check.</summary>
    public string? Owner(Uri server, int mangaId) =>
        _owners.TryGetValue((server, mangaId), out var sourceId) ? sourceId : null;

    public void SetOwner(Uri server, int mangaId, string sourceId) => Bounded(_owners)[(server, mangaId)] = sourceId;

    private static ConcurrentDictionary<TKey, TValue> Bounded<TKey, TValue>(ConcurrentDictionary<TKey, TValue> map)
        where TKey : notnull
    {
        if (map.Count >= MaxEntries)
        {
            map.Clear();
        }

        return map;
    }

    public void Clear()
    {
        _manga.Clear();
        _chapters.Clear();
        _owners.Clear();
    }
}
