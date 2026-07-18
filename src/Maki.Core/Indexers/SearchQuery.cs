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

    private static string NormalizePunctuation(string title) => string.Join(' ',
        title
            .Replace('‘', '\'').Replace('’', '\'')   // ‘ ’
            .Replace('“', '"').Replace('”', '"')     // “ ”
            .Replace('–', '-').Replace('—', '-')     // – —
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
