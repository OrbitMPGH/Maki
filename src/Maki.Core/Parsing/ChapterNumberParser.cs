using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Maki.Core.Parsing;

public record ParsedChapter(decimal? Number, int? Volume, bool IsOneShot, int? Part = null, int? SubPart = null)
{
    /// <summary>
    /// The number with its part folded in at the next decimal place ("9" part 2 is 9.2, "2.5" part 1
    /// is 2.51), so the parts of one chapter stay separate rows and never land on another chapter's
    /// number. A sub-part takes the place after that ("第60話後編②" is 60.32).
    /// </summary>
    public decimal? NumberWithPart => Number is { } number ? Append(Append(number, Part), SubPart) : null;

    private static decimal Append(decimal number, int? digit)
    {
        if (digit is not { } d)
        {
            return number;
        }

        // Dividing by 1.000... drops trailing zeros, so "9.0" counts as 9 and its part goes in the tenths.
        var trimmed = number / 1.0000000000000000000000000000m;
        var place = 0.1m;
        for (var i = 0; i < trimmed.Scale; i++)
        {
            place /= 10;
        }

        return trimmed + d * place;
    }
}

/// <summary>
/// The single place chapter identifiers get parsed. Sources feed it whatever string
/// (and optional separate volume string) they have; nothing else in the app should
/// attempt its own chapter-number parsing.
/// </summary>
public static partial class ChapterNumberParser
{
    // "Episode 12" is how WeebCentral labels webtoons; read as a one-shot, every episode of a series
    // turned into its own unnumbered chapter.
    [GeneratedRegex(@"(?:\b(?:ch(?:apter)?|ep(?:isode)?)(?![a-z])[.・]?\s*)([0-9]+(?:\.[0-9]+|,[0-9]{1,2}(?![0-9]))?)", RegexOptions.IgnoreCase)]
    private static partial Regex ChapterPattern();

    // "第12話", "【第14話(1)】", "第 13", "第5回" and "12話" as Japanese sources label them.
    [GeneratedRegex(@"第\s*([0-9]+(?:\.[0-9]+)?)\s*(?:[話回]|$|[:\s])|([0-9]+(?:\.[0-9]+)?)\s*話")]
    private static partial Regex JapaneseChapterPattern();

    // A label that is only a volume ("1巻", "第12巻"), not one that mentions one ("2巻発売記念").
    [GeneratedRegex(@"^(?:第\s*)?([0-9]+)\s*巻(?:\s|$)")]
    private static partial Regex JapaneseVolumePattern();

    // A part written right after the chapter number: "第9話 -1", "第2話(2)", "【episode22(2)】title".
    // No space after the hyphen, or "Ch. 5 - 3 Little Pigs" would be part 3.
    [GeneratedRegex(@"^\s*(?:[-ー]([1-9])|\(\s*([1-9])\s*\))(?![0-9])")]
    private static partial Regex AdjacentPartPattern();

    // A part closing the label after the title: "episode4 制御不能な恋心(2)". One digit only, since a
    // chapter is never split ten ways and "Lv(10)" is part of a title.
    [GeneratedRegex(@"\(\s*([1-9])\s*\)\s*[】\]]?\s*$")]
    private static partial Regex TrailingPartPattern();

    // "前編", "中編", "後編": the first, middle and last part of a chapter.
    [GeneratedRegex("([前中後])編")]
    private static partial Regex PartWordPattern();

    // A circled digit after the number or a part word: "第2話②", "第60話後編②", and the dingbat
    // variant ComicWalker uses on some series ("第69話➁"). Read before NFKC, which folds it to a digit.
    [GeneratedRegex(@"(?<=[話回編0-9０-９]\s*)[①-⑨➀-➈]")]
    private static partial Regex CircledPartPattern();

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

        // A circled part comes out first, or NFKC turns "第2話②" into "第2話2". Fullwidth digits and
        // letters fold to ASCII, as the GigaViewer source already does for its own labels; otherwise a
        // fullwidth number reads as a titled one-shot.
        var circled = CircledPartPattern().Match(chapterRaw);
        int? circledPart = circled.Success ? CircledValue(circled.Value[0]) : null;
        var text = (circled.Success ? chapterRaw.Remove(circled.Index, circled.Length) : chapterRaw)
            .Normalize(NormalizationForm.FormKC).Trim();

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
            return WithParts(marked, volume, text, chapterMatch, circledPart);
        }

        var japanese = JapaneseChapterPattern().Match(text);
        if (japanese.Success &&
            TryParseNumber(japanese.Groups[japanese.Groups[1].Success ? 1 : 2].Value) is { } japaneseNumber)
        {
            return WithParts(japaneseNumber, volume, text, japanese, circledPart);
        }

        // "100 - The Ending", "#12", "5.5: Extras"
        var bare = BareNumberPattern().Match(text);
        if (bare.Success && TryParseNumber(bare.Groups[1].Value) is { } bareNumber)
        {
            return new ParsedChapter(bareNumber, volume, false);
        }

        if (volume is null && JapaneseVolumePattern().Match(text) is { Success: true } tankobon &&
            int.TryParse(tankobon.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tankobonVolume))
        {
            volume = tankobonVolume;
        }

        // Unparseable: treat as a one-shot/special so it is not lost. A volume-only row keeps its volume.
        return new ParsedChapter(null, volume, IsOneShot: volume is null);
    }

    /// <summary>
    /// A part word (前編/中編/後編) is the part and a numbered part under it the sub-part, as
    /// ComicWalker splits its long chapters; without a word the numbered part is the part.
    /// </summary>
    private static ParsedChapter WithParts(decimal number, int? volume, string text, Match marker, int? circledPart)
    {
        var rest = text[(marker.Index + marker.Length)..];
        var numbered = circledPart ?? NumberedPart(rest);
        var word = PartWordPattern().Match(text);
        return word.Success
            ? new ParsedChapter(number, volume, false, "前中後".IndexOf(word.Groups[1].Value[0]) + 1, numbered)
            : new ParsedChapter(number, volume, false, numbered);
    }

    private static int? NumberedPart(string afterMarker)
    {
        var match = AdjacentPartPattern().Match(afterMarker);
        if (!match.Success)
        {
            match = TrailingPartPattern().Match(afterMarker);
        }

        if (!match.Success)
        {
            return null;
        }

        var digit = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        return digit[0] - '0';
    }

    private static int CircledValue(char circled) =>
        circled >= '➀' ? circled - '➀' + 1 : circled - '①' + 1;

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
    // A single digit is always a decimal ("4,5" is 4.5). Two digits that continue the number
    // ("9,10", "10,11") are two chapters in one row ("Ch.9,10") and take the first.
    private static decimal? TryParseNumber(string digits) =>
        decimal.TryParse(CommaDecimal().Replace(digits, CommaNumber), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    private static string CommaNumber(Match match)
    {
        var whole = match.Groups[1].Value;
        var after = match.Groups[2].Value;
        var consecutive = after.Length == 2 && after[0] != '0' &&
                          decimal.TryParse(whole, NumberStyles.None, CultureInfo.InvariantCulture, out var first) &&
                          decimal.TryParse(after, NumberStyles.None, CultureInfo.InvariantCulture, out var second) &&
                          second == first + 1;
        return consecutive ? whole : $"{whole}.{after}";
    }

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
