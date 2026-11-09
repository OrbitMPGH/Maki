namespace Maki.Core.Sources;

/// <summary>
/// Optional source capability: the chapter list is paged newest first, so a sync that already holds
/// the mapping's links can stop at the first page that brings nothing new. MangaFire walks its pager
/// in a headless browser, where every extra page is a click and a signed request.
/// <para>
/// A listing that stopped early says so (<see cref="ChapterListing.Complete"/> false). The sync then
/// merges it: links are added and updated, none are removed, and it is never cached as the mapping's
/// whole list. A complete listing is handled like any <see cref="ISource.ListChaptersAsync"/> result.
/// </para>
/// </summary>
public interface INewestFirstChapterSource
{
    Task<ChapterListing> ListNewChaptersAsync(
        string sourceSeriesId, string? languageFilter, KnownChapters known, CancellationToken ct = default);
}

/// <summary>A chapter listing, and whether it is the source's whole list or only its newest pages.</summary>
public sealed record ChapterListing(IReadOnlyList<SourceChapter> Chapters, bool Complete);

/// <summary>
/// What a mapping already links. A listed chapter is known by its source id, or by number, volume and
/// language, since a site can list a second upload of a chapter the mapping already has under one id.
/// A chapter with no number is known only by its id: every unnumbered extra shares the same key, so a
/// new one would read as known.
/// </summary>
public sealed class KnownChapters
{
    private readonly HashSet<string> _ids;
    private readonly HashSet<(decimal? Number, int? Volume, string Language)> _keys;

    public KnownChapters(
        IEnumerable<string> ids, IEnumerable<(decimal? Number, int? Volume, string Language)> keys)
    {
        _ids = new HashSet<string>(ids, StringComparer.Ordinal);
        _keys = keys.Where(k => k.Number is not null)
            .Select(k => (k.Number, k.Volume, SourceLanguages.Canonical(k.Language))).ToHashSet();
    }

    public int Count => _ids.Count;

    public bool Knows(SourceChapter chapter) =>
        _ids.Contains(chapter.SourceChapterId) ||
        (chapter.Number is not null &&
         _keys.Contains((chapter.Number, chapter.Volume, SourceLanguages.Canonical(chapter.Language))));

    /// <summary>
    /// True when a page of the listing can end the walk: it holds at least one chapter the caller
    /// wants, and every one of them is known. A page with nothing wanted on it says nothing.
    /// </summary>
    public bool CoversPage(IReadOnlyCollection<SourceChapter> page) => page.Count > 0 && page.All(Knows);
}
