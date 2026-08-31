using System.Text;
using System.Text.RegularExpressions;

namespace Maki.Core.Scrobbling;

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
    /// Minimum fraction of the shorter title's words that must also appear in the longer
    /// title. Char-level similarity alone scores a shared prefix with a swapped last word
    /// ("Boy Meets Maria" vs "Boy Meets Girl") almost as high as a real subtitle variant
    /// ("Hajime no Ippo" vs "...: Fighting Spirit!") - the word set must actually be a
    /// subset (allowing for appended words), not just a substring match.
    /// </summary>
    private const double WordCoverageThreshold = 0.9;

    /// <summary>
    /// The score at which a title stands on its own. Below it (but still above the caller's
    /// threshold) a pair is only accepted when one title is the other plus a tail - see
    /// <see cref="ExtendsOrIsExtendedBy"/>.
    /// <para>
    /// The caller's threshold has to be low enough for subtitle variants ("Hajime no Ippo" against
    /// "Hajime no Ippo: Fighting Spirit!" scores 0.64), and everything that title covers a *fragment*
    /// of scores higher than that: "High School Boy" scores 0.65 against "She's Adopted a High School
    /// Boy!" and 0.71 against "Magic, High School, and a Boy", and word coverage cannot see the
    /// difference either, since it divides by the shorter title's word count and a short title
    /// contained in a longer one always covers itself completely. No threshold separates them because
    /// the wrong answers genuinely score higher than the right one. What separates them is shape.
    /// </para>
    /// </summary>
    private const double StandaloneThreshold = 0.85;

    /// <summary>
    /// Whether one normalized title is the other followed by more words - the shape a subtitle,
    /// edition or season suffix takes ("Hajime no Ippo" / "Hajime no Ippo Fighting Spirit").
    /// <para>
    /// Both directions count: our title is whichever of the two MangaBaka happens to hold, so the
    /// source is as likely to be listing the shorter form as the longer one. How far apart they may
    /// be is left to the score - "Naruto" extends to "Naruto Gaiden The Seventh Hokage" as well, and
    /// what rules that out is that it only scores 0.32.
    /// </para>
    /// <para>
    /// The boundary check is what makes this a rule about words rather than characters: without it
    /// "Blue Lock" would extend to "Blue Locker".
    /// </para>
    /// </summary>
    private static bool ExtendsOrIsExtendedBy(string a, string b)
    {
        var na = NormalizeTitle(a);
        var nb = NormalizeTitle(b);
        if (na.Length == 0 || nb.Length == 0)
        {
            return false;
        }

        if (na == nb)
        {
            return true;
        }

        var (shorter, longer) = na.Length < nb.Length ? (na, nb) : (nb, na);
        return longer.StartsWith(shorter, StringComparison.Ordinal) && longer[shorter.Length] == ' ';
    }

    private static HashSet<string> Words(string title) =>
        [.. NormalizeTitle(title).Split(' ', StringSplitOptions.RemoveEmptyEntries)];

    private static double WordCoverage(string a, string b)
    {
        var wa = Words(a);
        var wb = Words(b);
        if (wa.Count == 0 || wb.Count == 0)
        {
            return 0;
        }

        return (double)wa.Intersect(wb).Count() / Math.Min(wa.Count, wb.Count);
    }

    /// <summary>
    /// Picks the best-scoring candidate (max similarity over query titles × candidate
    /// titles), or null when nothing gets past the threshold, word-coverage and shape checks.
    /// </summary>
    public static ScrobbleCandidate? BestCandidate(
        string title, string? altTitle, IReadOnlyList<ScrobbleCandidate> candidates,
        double threshold = MatchThreshold)
    {
        var queries = altTitle is null ? new[] { title } : [title, altTitle];

        // Never below what the caller asked for: a caller stricter than StandaloneThreshold (the
        // scrobbler is, at 0.93) gets its own threshold on both branches, so the relaxation is
        // invisible to it.
        var standalone = Math.Max(threshold, StandaloneThreshold);

        ScrobbleCandidate? best = null;
        var bestScore = 0.0;
        foreach (var candidate in candidates)
        {
            var names = new[] { candidate.Title }.Concat(candidate.AltTitles);
            var score = queries
                .SelectMany(q => names.Select(n => (Score: TitleSimilarity(q, n), Q: q, N: n)))
                .Where(pair => pair.Score >= threshold && WordCoverage(pair.Q, pair.N) >= WordCoverageThreshold)
                .Where(pair => pair.Score >= standalone || ExtendsOrIsExtendedBy(pair.Q, pair.N))
                .Select(pair => pair.Score)
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
