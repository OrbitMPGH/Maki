using System.Globalization;
using System.Text;
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
    // "Episode 12" is how WeebCentral labels webtoons; read as a one-shot, every episode of a series
    // turned into its own unnumbered chapter.
    [GeneratedRegex(@"(?:\b(?:ch(?:apter)?|ep(?:isode)?)(?![a-z])\.?\s*)([0-9]+(?:\.[0-9]+|,[0-9]{1,2}(?![0-9]))?)", RegexOptions.IgnoreCase)]
    private static partial Regex ChapterPattern();

    [GeneratedRegex(@"^\s*#?([0-9]+(?:\.[0-9]+|,[0-9]{1,2}(?![0-9]))?)\s*(?:[-:–].*)?$")]
    private static partial Regex BareNumberPattern();

    [GeneratedRegex(@"^([0-9]+),([0-9]{1,2})$")]
    private static partial Regex CommaDecimal();

    [GeneratedRegex(@"\bvol(?:ume)?\b\.?\s*([0-9]+)", RegexOptions.IgnoreCase)]
    private static partial Regex VolumePattern();

    [GeneratedRegex(@"\bone[\s-]?shot\b", RegexOptions.IgnoreCase)]
    private static partial Regex OneShotPattern();

    // "/chapter-225", "one-piece-chapter-1187", "chapter-12-5/" (12.5), MangaKatana's "/c1050.5"
    [GeneratedRegex(@"(?:(?:^|[/-])chapter-([0-9]+)(?:-([0-9]+))?|/c([0-9]+)(?:\.([0-9]+))?)/?$", RegexOptions.IgnoreCase)]
    private static partial Regex SlugPattern();

    public static ParsedChapter Parse(string? chapterRaw, string? volumeRaw = null)
    {
        int? volume = TryParseVolume(volumeRaw);
        if (string.IsNullOrWhiteSpace(chapterRaw))
        {
            return new ParsedChapter(null, volume, IsOneShot: volume is null);
        }

        // Fullwidth digits and letters fold to ASCII, as the ComicWalker and GigaViewer sources already
        // do for their own labels; otherwise a fullwidth number reads as a titled one-shot.
        var text = chapterRaw.Normalize(NormalizationForm.FormKC).Trim();

        if (OneShotPattern().IsMatch(text))
        {
            return new ParsedChapter(null, volume, IsOneShot: true);
        }

        // Only an explicit "Vol." marker counts when scanning chapter text —
        // a bare number here is the chapter, not a volume.
        if (volume is null)
        {
            var embedded = VolumePattern().Match(text);
            if (embedded.Success && int.TryParse(embedded.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var embeddedVolume))
            {
                volume = embeddedVolume;
            }
        }

        // Direct decimal ("10", "10.5", "10,5"): the common case for API-backed sources.
        if (TryParseNumber(text) is { } direct)
        {
            return new ParsedChapter(direct, volume, false);
        }

        var chapterMatch = ChapterPattern().Match(text);
        if (chapterMatch.Success && TryParseNumber(chapterMatch.Groups[1].Value) is { } marked)
        {
            return new ParsedChapter(marked, volume, false);
        }

        // "100 - The Ending", "#12", "5.5: Extras"
        var bare = BareNumberPattern().Match(text);
        if (bare.Success && TryParseNumber(bare.Groups[1].Value) is { } bareNumber)
        {
            return new ParsedChapter(bareNumber, volume, false);
        }

        // Unparseable: treat as a one-shot/special so it is not lost. A volume-only row keeps its volume.
        return new ParsedChapter(null, volume, IsOneShot: volume is null);
    }

    /// <summary>
    /// Falls back to the number in a chapter's URL or slug when its label has none. Some sites label
    /// a row with only the chapter's title ("Anna-chan Can't Study"), and without this it becomes an
    /// unnumbered special beside the real chapter.
    /// </summary>
    public static ParsedChapter OrSlugNumber(this ParsedChapter parsed, string? urlOrSlug) =>
        parsed.Number is null && FromSlug(urlOrSlug) is { } number
            ? parsed with { Number = number, IsOneShot = false }
            : parsed;

    public static decimal? FromSlug(string? urlOrSlug)
    {
        if (string.IsNullOrEmpty(urlOrSlug))
        {
            return null;
        }

        var match = SlugPattern().Match(urlOrSlug);
        if (!match.Success)
        {
            return null;
        }

        var (whole, fraction) = match.Groups[1].Success
            ? (match.Groups[1], match.Groups[2])
            : (match.Groups[3], match.Groups[4]);
        var text = fraction.Success ? $"{whole.Value}.{fraction.Value}" : whole.Value;
        return decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
    }

    // A comma is a decimal separator only with one or two digits after it; "1,000" stays unparsed.
    private static decimal? TryParseNumber(string digits) =>
        decimal.TryParse(CommaDecimal().Replace(digits, "$1.$2"), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    private static int? TryParseVolume(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Normalize(NormalizationForm.FormKC);
        if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var direct))
        {
            return direct;
        }

        var match = VolumePattern().Match(text);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}
