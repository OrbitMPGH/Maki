using System.Globalization;
using System.Text.RegularExpressions;

namespace Maki.Core.Parsing;

public record ParsedChapter(decimal? Number, int? Volume, bool IsOneShot);

/// <summary>
/// The single place chapter identifiers get parsed. Sources feed it whatever string
/// (and optional separate volume string) they have; nothing else in the app should
/// attempt its own chapter-number parsing.
/// </summary>
public static partial class ChapterNumberParser
{
    [GeneratedRegex(@"(?:\bch(?:apter)?\b\.?\s*)(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ChapterPattern();

    [GeneratedRegex(@"^\s*#?(\d+(?:\.\d+)?)\s*(?:[-:–].*)?$")]
    private static partial Regex BareNumberPattern();

    [GeneratedRegex(@"\bvol(?:ume)?\b\.?\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex VolumePattern();

    [GeneratedRegex(@"\bone[\s-]?shot\b", RegexOptions.IgnoreCase)]
    private static partial Regex OneShotPattern();

    public static ParsedChapter Parse(string? chapterRaw, string? volumeRaw = null)
    {
        int? volume = TryParseVolume(volumeRaw);
        if (string.IsNullOrWhiteSpace(chapterRaw))
        {
            return new ParsedChapter(null, volume, IsOneShot: volume is null);
        }

        var text = chapterRaw.Trim();

        if (OneShotPattern().IsMatch(text))
        {
            return new ParsedChapter(null, volume, IsOneShot: true);
        }

        // Only an explicit "Vol." marker counts when scanning chapter text —
        // a bare number here is the chapter, not a volume.
        if (volume is null)
        {
            var embedded = VolumePattern().Match(text);
            if (embedded.Success)
            {
                volume = int.Parse(embedded.Groups[1].Value, CultureInfo.InvariantCulture);
            }
        }

        // Direct decimal ("10", "10.5") — the common case for API-backed sources.
        if (decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var direct))
        {
            return new ParsedChapter(direct, volume, false);
        }

        var chapterMatch = ChapterPattern().Match(text);
        if (chapterMatch.Success)
        {
            return new ParsedChapter(
                decimal.Parse(chapterMatch.Groups[1].Value, CultureInfo.InvariantCulture),
                volume,
                false);
        }

        // "100 - The Ending", "#12", "5.5: Extras"
        var bare = BareNumberPattern().Match(text);
        if (bare.Success)
        {
            return new ParsedChapter(
                decimal.Parse(bare.Groups[1].Value, CultureInfo.InvariantCulture),
                volume,
                false);
        }

        // Unparseable and no volume info: treat as a one-shot/special so it is not lost.
        return new ParsedChapter(null, volume, IsOneShot: true);
    }

    private static int? TryParseVolume(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var direct))
        {
            return direct;
        }

        var match = VolumePattern().Match(text);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }
}
