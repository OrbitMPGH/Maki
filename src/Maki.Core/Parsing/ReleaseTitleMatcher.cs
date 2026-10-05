using System.Globalization;
using Maki.Core.Entities;
using Maki.Core.Scrobbling;

namespace Maki.Core.Parsing;

/// <summary>
/// Whether a parsed torrent title names a series. Every title candidate is scored through
/// <see cref="ScrobbleMatching.BestCandidate"/>, so the word-coverage and shape rules source matching
/// relies on apply here unchanged.
/// </summary>
public static class ReleaseTitleMatcher
{
    public const double Threshold = 0.6;

    public static bool Matches(ParsedReleaseTitle release, string seriesTitle, string? originalTitle,
        IReadOnlyList<LocalizedTitle> altTitles)
    {
        if (release.TitleCandidates.Count == 0) return false;

        var candidates = release.TitleCandidates
            .Select((title, i) => new ScrobbleCandidate(i.ToString(CultureInfo.InvariantCulture), title, [], ""))
            .ToList();

        if (ScrobbleMatching.BestCandidate(seriesTitle, originalTitle, candidates, Threshold) is not null) return true;

        return altTitles
            .Where(alt => !string.IsNullOrWhiteSpace(alt.Title))
            .Any(alt => ScrobbleMatching.BestCandidate(alt.Title, null, candidates, Threshold) is not null);
    }
}
