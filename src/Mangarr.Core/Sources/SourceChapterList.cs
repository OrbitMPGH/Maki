namespace Mangarr.Core.Sources;

/// <summary>
/// The dedupe + ordering every <see cref="ISource"/> applies before returning a chapter list.
/// <para>
/// Sources list the same chapter more than once — different scanlation groups on MangaDex,
/// official and unofficial rips on MangaFire — so each keeps one entry per
/// (Number, Volume, Language) and returns them ascending by number. Only the choice of *which*
/// duplicate wins is source-specific; that's <c>preferred</c>. This lived as four near-identical
/// LINQ chains, where a fix to one never reached the others.
/// </para>
/// </summary>
public static class SourceChapterList
{
    /// <summary>
    /// Dedupes by (Number, Volume, Language) and orders ascending by number.
    /// <paramref name="preferred"/> picks the winner among duplicates; omit it to keep the first
    /// one the source listed.
    /// </summary>
    public static List<SourceChapter> Normalize(
        IEnumerable<SourceChapter> chapters,
        Func<IEnumerable<SourceChapter>, SourceChapter>? preferred = null) =>
        Normalize(chapters, c => c, preferred ?? (g => g.First()));

    /// <summary>
    /// As <see cref="Normalize(IEnumerable{SourceChapter}, Func{IEnumerable{SourceChapter}, SourceChapter}?)"/>,
    /// for sources that carry extra per-chapter data needed to choose between duplicates (and so
    /// can't group over bare <see cref="SourceChapter"/>).
    /// </summary>
    public static List<SourceChapter> Normalize<T>(
        IEnumerable<T> items,
        Func<T, SourceChapter> toChapter,
        Func<IEnumerable<T>, T> preferred) =>
        items
            .GroupBy(item => Key(toChapter(item)))
            .Select(group => toChapter(preferred(group)))
            .OrderBy(c => c.Number)
            .ToList();

    private static (decimal? Number, int? Volume, string Language) Key(SourceChapter c) =>
        (c.Number, c.Volume, c.Language);
}
