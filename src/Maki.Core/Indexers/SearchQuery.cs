using Maki.Core.Entities;

namespace Maki.Core.Indexers;

/// <summary>Builds progressively looser indexer search queries from a series title.</summary>
public static class SearchQuery
{
    /// <summary>
    /// Progressively looser queries: the full title (typographic punctuation normalized,
    /// since indexers rarely store curly quotes), then the part before a subtitle
    /// separator (":", " - ", "~") — release names usually drop subtitles.
    /// </summary>
    public static IEnumerable<string> Candidates(string title)
    {
        var normalized = NormalizePunctuation(title);
        yield return normalized;

        var separators = new[] { ":", " - ", "~" };
        var cut = separators
            .Select(s => normalized.IndexOf(s, StringComparison.Ordinal))
            .Where(i => i > 0)
            .DefaultIfEmpty(-1)
            .Min();
        if (cut > 0)
        {
            var main = normalized[..cut].Trim();
            if (main.Length >= 2 && !main.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                yield return main;
            }
        }
    }

    /// <summary>
    /// <see cref="Candidates"/> for the title, then up to <paramref name="maxExtra"/> full-title queries from the
    /// original and alternate titles for when those find nothing. English titles come first, then
    /// romanized ones, then other Latin-script titles, then everything else; duplicates and titles too
    /// short to search on are dropped.
    /// </summary>
    public static IReadOnlyList<string> WithFallbacks(
        string title, string? originalTitle, IEnumerable<LocalizedTitle> altTitles, int maxExtra = 3)
    {
        var queries = Candidates(title).ToList();
        var seen = new HashSet<string>(queries, StringComparer.OrdinalIgnoreCase);

        var pool = new List<LocalizedTitle>();
        if (!string.IsNullOrWhiteSpace(originalTitle))
        {
            pool.Add(new LocalizedTitle(originalTitle, null));
        }

        pool.AddRange(altTitles);

        var extras = pool
            .Where(t => !string.IsNullOrWhiteSpace(t.Title))
            .Select(t => (Query: NormalizePunctuation(t.Title), Rank: Rank(t)))
            .Where(t => t.Query.Length >= MinFallbackLength)
            .OrderBy(t => t.Rank)
            .Select(t => t.Query)
            .Where(seen.Add)
            .Take(maxExtra);

        queries.AddRange(extras);
        return queries;
    }

    private const int MinFallbackLength = 3;

    private static int Rank(LocalizedTitle t)
    {
        if (LocalizedTitle.Matches(t.Language, "en"))
        {
            return 0;
        }

        if (t.Language?.EndsWith("-Latn", StringComparison.OrdinalIgnoreCase) == true)
        {
            return 1;
        }

        return t.Title.All(c => c < 'ɐ') ? 2 : 3;
    }

    private static string NormalizePunctuation(string title) => string.Join(' ',
        title
            .Replace('‘', '\'').Replace('’', '\'')   // ‘ ’
            .Replace('“', '"').Replace('”', '"')     // “ ”
            .Replace('–', '-').Replace('—', '-')     // – —
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
