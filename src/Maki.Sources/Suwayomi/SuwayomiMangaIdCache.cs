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
    private readonly ConcurrentDictionary<(Uri Server, string SourceId, string Url), int> _manga = new();
    private readonly ConcurrentDictionary<(Uri Server, int MangaId, string Url), int> _chapters = new();

    public int? Manga(Uri server, string sourceId, string url) =>
        _manga.TryGetValue((server, sourceId, url), out var id) ? id : null;

    public void SetManga(Uri server, string sourceId, string url, int id) => _manga[(server, sourceId, url)] = id;

    public void ForgetManga(Uri server, string sourceId, string url) => _manga.TryRemove((server, sourceId, url), out _);

    public int? Chapter(Uri server, int mangaId, string url) =>
        _chapters.TryGetValue((server, mangaId, url), out var id) ? id : null;

    public void SetChapter(Uri server, int mangaId, string url, int id) => _chapters[(server, mangaId, url)] = id;

    public void ForgetChapter(Uri server, int mangaId, string url) => _chapters.TryRemove((server, mangaId, url), out _);

    public void Clear()
    {
        _manga.Clear();
        _chapters.Clear();
    }
}
