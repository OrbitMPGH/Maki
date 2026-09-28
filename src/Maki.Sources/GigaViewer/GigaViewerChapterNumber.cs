using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Maki.Core.Parsing;

namespace Maki.Sources.GigaViewer;

/// <summary>
/// Chapter numbers for GigaViewer episode titles. Plain <see cref="ChapterNumberParser"/> fails
/// on the site's usual "140話" shape (no "ch"/"chapter" word, no bare leading digit) and returns
/// a one-shot null, so this runs first and falls back to it.
/// <para>
/// A title can additionally carry a "part" marker for a chapter split across an update: the word
/// 前編/中編/後編 ("first/middle/last part", optionally parenthesized) and/or a circled digit
/// (①-⑨ or ➀-➈). One marker nudges the number by a tenth; both nudge by a tenth and a hundredth,
/// so "第60話 後編②" (part 3, circle 2) reads as 60.32 without colliding with a real 60.3 or 60.2.
/// </para>
/// </summary>
public static partial class GigaViewerChapterNumber
{
    [GeneratedRegex(@"[\(（]?(前編|中編|後編)[\)）]?")]
    private static partial Regex PartWordPattern();

    [GeneratedRegex("[①②③④⑤⑥⑦⑧⑨➀➁➂➃➄➅➆➇➈]")]
    private static partial Regex PartCirclePattern();

    [GeneratedRegex(@"第?\s*(\d+(?:\.\d+)?)\s*[話回]")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"^vol\.?\s*(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex VolumeAsNumberPattern();

    public static decimal? Parse(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var text = title;

        int? wordPart = null;
        var wordMatch = PartWordPattern().Match(text);
        if (wordMatch.Success)
        {
            wordPart = wordMatch.Groups[1].Value switch
            {
                "前編" => 1,
                "中編" => 2,
                "後編" => 3,
                _ => null
            };
            text = text.Remove(wordMatch.Index, wordMatch.Length);
        }

        int? circlePart = null;
        var circleMatch = PartCirclePattern().Match(text);
        if (circleMatch.Success)
        {
            circlePart = CircleValue(circleMatch.Value[0]);
            text = text.Remove(circleMatch.Index, circleMatch.Length);
        }

        // Full-width digits (１４０ -> 140) collapse under NFKC; done after marker removal so a
        // circled full-width-adjacent digit isn't double counted.
        var normalized = text.Normalize(NormalizationForm.FormKC);

        decimal? number = null;
        var numberMatch = NumberPattern().Match(normalized);
        if (numberMatch.Success)
        {
            number = decimal.Parse(numberMatch.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        else
        {
            number = ChapterNumberParser.Parse(title).Number;
            if (number is null)
            {
                // MAGCOMI names serial episodes "Vol.25"; a real volume is a type=volume product,
                // which ListChaptersAsync already skips, so this can only be a serial number.
                var volumeMatch = VolumeAsNumberPattern().Match(normalized.Trim());
                if (volumeMatch.Success)
                {
                    number = decimal.Parse(volumeMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                }
            }
        }

        if (number is null)
        {
            return null;
        }

        return (wordPart, circlePart) switch
        {
            (not null, not null) => number + wordPart.Value * 0.1m + circlePart.Value * 0.01m,
            (not null, null) => number + wordPart.Value * 0.1m,
            (null, not null) => number + circlePart.Value * 0.1m,
            _ => number
        };
    }

    private static int CircleValue(char c) => c switch
    {
        >= '①' and <= '⑨' => c - '①' + 1,
        >= '➀' and <= '➈' => c - '➀' + 1,
        _ => 0
    };
}
