using Maki.Core.Entities;
using Maki.Core.Scrobbling;

namespace Maki.Core.Parsing;

/// <param name="Rival">The related title that named the release better than the series did, when one did.</param>
public sealed record ReleaseTitleMatch(bool Matched, string? Rival);

/// <summary>
/// Whether a parsed torrent title names a series. Every title candidate is scored through
/// <see cref="ScrobbleMatching.MatchScore"/>, so the word-coverage and shape rules source matching
/// relies on apply here unchanged.
/// </summary>
public static class ReleaseTitleMatcher
{
    public const double Threshold = 0.6;

    public static bool Matches(ParsedReleaseTitle release, string seriesTitle, string? originalTitle,
        IReadOnlyList<LocalizedTitle> altTitles) =>
        Match(release, Titles(seriesTitle, originalTitle, altTitles), []).Matched;

    /// <summary>
    /// Rivals are titles of other works (a spin-off, a sequel, another library series). The extension
    /// rule that lets "Hajime no Ippo: Fighting Spirit!" match "Hajime no Ippo" also lets
    /// "My Dress-Up Darling XOXO!" match "My Dress-Up Darling", and only a known rival can tell the two
    /// apart. Judged per candidate: the candidate that names the series has to name it at least as well
    /// as it names any rival, so a dual-title release is decided by the half that matched. Ties keep ours.
    /// </summary>
    public static ReleaseTitleMatch Match(ParsedReleaseTitle release, IReadOnlyList<string> ourTitles,
        IReadOnlyList<string> rivalTitles)
    {
        string? beatenBy = null;
        foreach (var candidate in release.TitleCandidates)
        {
            var ours = ScrobbleMatching.MatchScore(ourTitles, candidate, Threshold);
            if (ours < Threshold)
            {
                continue;
            }

            var (rival, rivalScore) = rivalTitles
                .Select(r => (Title: r, Score: ScrobbleMatching.MatchScore([r], candidate, Threshold)))
                .DefaultIfEmpty((Title: "", Score: 0))
                .MaxBy(r => r.Score);
            if (rivalScore <= ours)
            {
                return new ReleaseTitleMatch(true, null);
            }

            beatenBy ??= rival;
        }

        return new ReleaseTitleMatch(false, beatenBy);
    }

    /// <summary>
    /// The candidate titles that could take a release from the series: whole-word extensions of one of
    /// its titles in either direction, minus spellings that are already its own. Anything else is
    /// already stopped by the word-coverage and shape checks, so scoring it would only cost time.
    /// </summary>
    public static IReadOnlyList<string> Rivals(IReadOnlyList<string> ourTitles, IEnumerable<string> candidates)
    {
        var own = ourTitles.Select(ScrobbleMatching.NormalizeTitle).Where(t => t.Length > 0).ToHashSet(StringComparer.Ordinal);
        return candidates
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.Ordinal)
            .Select(c => (Title: c, Normalized: ScrobbleMatching.NormalizeTitle(c)))
            .Where(c => !own.Contains(c.Normalized) &&
                        own.Any(o => ScrobbleMatching.NormalizedExtendsOrIsExtendedBy(c.Normalized, o)))
            .Select(c => c.Title)
            .ToList();
    }

    public static IReadOnlyList<string> Titles(string seriesTitle, string? originalTitle, IReadOnlyList<LocalizedTitle> altTitles) =>
        new[] { seriesTitle, originalTitle }
            .Concat(altTitles.Select(a => a.Title))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
}
