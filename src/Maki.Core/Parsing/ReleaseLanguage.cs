using System.Text.RegularExpressions;
using Maki.Core.Quality;

namespace Maki.Core.Parsing;

/// <summary>
/// The language a torrent release is written in, read from its title, so a Japanese series is not
/// offered an English digital release and the other way round. Kana or a Japanese tag says Japanese,
/// an English tag says English, Hangul says Korean. Anything else says nothing: a title in plain Latin
/// letters is as often a romanized Japanese release as an English one, and Han alone can be Chinese.
/// </summary>
public static partial class ReleaseLanguage
{
    public const string Japanese = "ja";
    public const string English = "en";

    [GeneratedRegex(@"[぀-ヿｦ-ﾟ]")]
    private static partial Regex Kana();

    [GeneratedRegex(@"[一-鿿㐀-䶿]")]
    private static partial Regex Han();

    [GeneratedRegex(@"[가-힯ᄀ-ᇿ]")]
    private static partial Regex Hangul();

    // "[Japanese]", "(JP)", "[ja]", "(Raw)", "[JPN-Raw]", "(Japanese Edition)", "日本語": the release says it is
    // Japanese. Only a word inside brackets counts, so a title that merely contains it ("Japanese Folktales",
    // "Raw Hero") is not read as one. "ja" alone is too short to trust anywhere but a tag of its own.
    [GeneratedRegex(@"[\[(][^\[\]()]*\b(?:japanese|jpn|jp|raws?)\b[^\[\]()]*[\])]|[\[(]\s*ja\s*[\])]|日本語", RegexOptions.IgnoreCase)]
    private static partial Regex JapaneseTag();

    [GeneratedRegex(@"[\[(]\s*(?:english|eng|en)\s*[\])]", RegexOptions.IgnoreCase)]
    private static partial Regex EnglishTag();

    // "(Japanese-English)", "[JP/EN]": a bilingual release, which is neither.
    [GeneratedRegex(@"[\[(][^\[\]()]*\b(?:japanese|jpn|jp|ja)\b[^\[\]()]*\b(?:english|eng|en)\b[^\[\]()]*[\])]|[\[(][^\[\]()]*\b(?:english|eng|en)\b[^\[\]()]*\b(?:japanese|jpn|jp|ja)\b[^\[\]()]*[\])]", RegexOptions.IgnoreCase)]
    private static partial Regex BilingualTag();

    public const string Korean = "ko";

    /// <summary>"ja", "en" or "ko", or null when the title does not say.</summary>
    public static string? Detect(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        if (Kana().IsMatch(title))
        {
            return Japanese;
        }

        if (Hangul().IsMatch(title))
        {
            return Korean;
        }

        if (BilingualTag().IsMatch(title))
        {
            return null;
        }

        var japanese = JapaneseTag().IsMatch(title);
        var english = EnglishTag().IsMatch(title);
        if (japanese != english)
        {
            return japanese ? Japanese : English;
        }

        return null;
    }

    /// <summary>
    /// What a release's language means for a series, given the languages of the chapter rows that have
    /// a file (null or empty for a row with none, which Maki reads as English). A series whose files are
    /// all marked Japanese, or all marked English, ignores a release that clearly says another language.
    /// A series with any file marked Japanese never takes a release unattended unless it is clearly
    /// Japanese and the series has no files in another language. Anything else is left as it was.
    /// </summary>
    /// <returns>Null when the language changes nothing, otherwise whether to ignore and the reason.</returns>
    public static (bool Ignore, string Reason)? Judge(IEnumerable<string?> fileLanguages, string? releaseTitle)
    {
        var marked = fileLanguages.Select(Base).Distinct().ToList();
        var kinds = marked.Select(l => l ?? English).Distinct().ToList();
        var release = Detect(releaseTitle);
        if (kinds.Count == 1 && marked.All(l => l is Japanese or English) && release is not null && release != kinds[0])
        {
            return (true, SpanVerdictReasons.LanguageMismatch);
        }

        if (!marked.Contains(Japanese))
        {
            return null;
        }

        if (kinds.Count > 1)
        {
            return (false, SpanVerdictReasons.LanguageMixed);
        }

        return release == Japanese ? null : (false, SpanVerdictReasons.LanguageUnknown);
    }

    private static string? Base(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : code.Split('-', '_')[0].ToLowerInvariant();
}
