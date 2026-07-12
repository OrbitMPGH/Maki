using System.Text;
using System.Text.RegularExpressions;

namespace Mangarr.Core.Scrobbling;

/// <summary>
/// Title/URL matching helpers for scrobbling: weblink id extraction and the strict
/// title-similarity check used to auto-accept search results.
/// </summary>
public static partial class ScrobbleMatching
{
    /// <summary>Minimum similarity (0-1) for a search result to be accepted without review.</summary>
    public const double MatchThreshold = 0.93;

    [GeneratedRegex(@"anilist\.co/manga/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex AniListLink();

    [GeneratedRegex(@"myanimelist\.net/manga/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex MalLink();

    [GeneratedRegex(@"mangabaka\.(?:org|dev)/(?:series/)?(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex MangaBakaLink();

    private static readonly (string Service, Regex Pattern)[] LinkPatterns =
    [
        ("anilist", AniListLink()),
        ("mal", MalLink()),
        ("mangabaka", MangaBakaLink()),
    ];

    /// <summary>Extracts {service: id} from a list of URLs (first mention per service wins).</summary>
    public static Dictionary<string, string> ParseWebLinks(IEnumerable<string> links)
    {
        var found = new Dictionary<string, string>();
        foreach (var url in links)
        {
            foreach (var (service, pattern) in LinkPatterns)
            {
                if (!found.ContainsKey(service) && pattern.Match(url) is { Success: true } m)
                {
                    found[service] = m.Groups[1].Value;
                }
            }
        }

        return found;
    }

    [GeneratedRegex(@"[^\w\s]")]
    private static partial Regex NonWord();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public static string NormalizeTitle(string title)
    {
        var t = title.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        t = NonWord().Replace(t, " ");
        return Whitespace().Replace(t, " ").Trim();
    }

    /// <summary>Normalized-title similarity in [0, 1] (Ratcliff/Obershelp, like Python's difflib).</summary>
    public static double TitleSimilarity(string a, string b)
    {
        var na = NormalizeTitle(a);
        var nb = NormalizeTitle(b);
        if (na.Length == 0 || nb.Length == 0)
        {
            return 0;
        }

        if (na == nb)
        {
            return 1;
        }

        return 2.0 * MatchingCharacters(na, 0, na.Length, nb, 0, nb.Length) / (na.Length + nb.Length);
    }

    /// <summary>
    /// Picks the best-scoring candidate (max similarity over query titles × candidate
    /// titles), or null when nothing reaches the threshold.
    /// </summary>
    public static ScrobbleCandidate? BestCandidate(
        string title, string? altTitle, IReadOnlyList<ScrobbleCandidate> candidates,
        double threshold = MatchThreshold)
    {
        var queries = altTitle is null ? new[] { title } : [title, altTitle];
        ScrobbleCandidate? best = null;
        var bestScore = 0.0;
        foreach (var candidate in candidates)
        {
            var names = new[] { candidate.Title }.Concat(candidate.AltTitles);
            var score = queries
                .SelectMany(q => names.Select(n => TitleSimilarity(q, n)))
                .DefaultIfEmpty(0)
                .Max();
            if (score > bestScore)
            {
                (best, bestScore) = (candidate, score);
            }
        }

        return bestScore >= threshold ? best : null;
    }

    /// <summary>Ratcliff/Obershelp: longest common substring, then recurse on both flanks.</summary>
    private static int MatchingCharacters(string a, int aLo, int aHi, string b, int bLo, int bHi)
    {
        if (aLo >= aHi || bLo >= bHi)
        {
            return 0;
        }

        // Longest common substring within the ranges (DP over one row).
        int bestI = aLo, bestJ = bLo, bestSize = 0;
        var row = new int[bHi - bLo + 1];
        for (var i = aLo; i < aHi; i++)
        {
            var prevDiag = 0;
            for (var j = bLo; j < bHi; j++)
            {
                var current = row[j - bLo + 1];
                if (a[i] == b[j])
                {
                    var size = prevDiag + 1;
                    row[j - bLo + 1] = size;
                    if (size > bestSize)
                    {
                        (bestI, bestJ, bestSize) = (i - size + 1, j - size + 1, size);
                    }
                }
                else
                {
                    row[j - bLo + 1] = 0;
                }

                prevDiag = current;
            }
        }

        if (bestSize == 0)
        {
            return 0;
        }

        return bestSize
               + MatchingCharacters(a, aLo, bestI, b, bLo, bestJ)
               + MatchingCharacters(a, bestI + bestSize, aHi, b, bestJ + bestSize, bHi);
    }
}
