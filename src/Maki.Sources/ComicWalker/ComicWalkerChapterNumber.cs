using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Maki.Core.Parsing;

namespace Maki.Sources.ComicWalker;

/// <summary>
/// Comic Walker episode titles encode the number as "第N話". Two things can refine it
/// further: a part marker (前編 1, 中編 2, 後編 3) and a circled digit (①-⑨, or the
/// dingbat variant ➀-➈ the site also uses) marking a sub-part within that part. A circled
/// digit with no part marker sits in the tenths place (第73話② -> 73.2); combined with a
/// part marker it drops to the hundredths place so the two don't collide
/// (第60話後編② -> 60 + 0.3 + 0.02 = 60.32). Never read <c>internal.episodeNo</c>: it is
/// sequential over parts and announcements alike, so it disagrees with every tracker.
/// </summary>
public static partial class ComicWalkerChapterNumber
{
    [GeneratedRegex(@"第?\s*(\d+(?:\.\d+)?)\s*[話回]")]
    private static partial Regex EpisodePattern();

    public static decimal? Parse(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var circled = CircledDigit(title);
        var normalized = title.Normalize(NormalizationForm.FormKC);
        var match = EpisodePattern().Match(normalized);
        if (!match.Success)
        {
            return ChapterNumberParser.Parse(title).Number;
        }

        var number = decimal.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var part = PartMarker(title);

        if (part is int p)
        {
            number += p * 0.1m;
            if (circled is int subPart)
            {
                number += subPart * 0.01m;
            }
        }
        else if (circled is int alone)
        {
            number += alone * 0.1m;
        }

        return number;
    }

    private static int? PartMarker(string title)
    {
        if (title.Contains("前編", StringComparison.Ordinal))
        {
            return 1;
        }

        if (title.Contains("中編", StringComparison.Ordinal))
        {
            return 2;
        }

        if (title.Contains("後編", StringComparison.Ordinal))
        {
            return 3;
        }

        return null;
    }

    private static int? CircledDigit(string title)
    {
        foreach (var ch in title)
        {
            if (ch is >= '①' and <= '⑨')
            {
                return ch - '①' + 1;
            }

            if (ch is >= '➀' and <= '➈')
            {
                return ch - '➀' + 1;
            }
        }

        return null;
    }
}
