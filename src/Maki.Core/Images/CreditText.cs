using System.Text.RegularExpressions;

namespace Maki.Core.Images;

/// <summary>
/// Tells a scanlator's page from a publisher's by the text read off it (OCR lines joined with
/// <c>" | "</c>). Two views of the lowercased text: as read, for short tokens that need word
/// boundaries, and squashed, with whitespace and line breaks removed, for phrases, because OCR drops
/// spaces ("READAT") and banners break phrases over lines.
/// <para>
/// The lists were tuned on 225 hand-labelled repeated pages (2026-11-13, git-ignored
/// <c>research/credit-pages</c>): 115 of 116 scanlator pages, 0 of 53 publisher pages and 1 of 44
/// title pages (a cover carrying the scanlator's credit) came out as scanlator. Change them only
/// against that set and the OCR verdicts of the library scan in <c>2026-11-13-lone-credits</c>
/// (<c>analyse textcheck</c>); "originalnovelby" was added there to keep an official webtoon title page.
/// The staff block (<see cref="StaffBlock"/>) was added against those and the tail bottoms of the 2026-11-14 scan
/// (<c>2026-11-14-efficiency/textcheck</c>): it flips only official staff lists, from accepted to rejected.
/// </para>
/// </summary>
public static partial class CreditText
{
    /// <summary>Bumped with every change to the lists; stored verdicts below it are judged again from their stored text.</summary>
    public const int Version = 3;

    private static readonly string[] StrongPhrases =
    [
        "discord", "patreon", "ko-fi", "kofi", "paypal", "recruit", "scanlat", "mangadex", "bato.to", "repost", "reupload", "re-upload", "uploadover",
        "joinus", "joinour", "supportus", "donat", "buyusa", "fantranslat", "lookingfor", "earlyaccess",
        "fasterrelease", "fasterupdate", "fastestrelease", "fastrelease",
        "officialrelease", "officialsite", "officialplatform", "officialvolume", "officialtranslation", "offical",
        "supporttheauthor", "supporttheartist", "supporttheofficial", "readat", "readfirst", "readonlyat", "readonlyon",
    ];

    private static readonly Regex[] StrongWords = [Words(@"scans\b"), Words(@"\bmtl\b")];

    private static readonly string[] WeakPhrases =
        ["proofreader", "cleaner", "qualitycheck", "typeset", "redraw", "oursite", "ourwebsite", "twitter", "translator:", "translator;"];

    private static readonly Regex[] WeakWords =
    [
        Words(@"\btl\s*[:;/]"), Words(@"\bpr\s*[:;]"), Words(@"\bts\s*[:;]"), Words(@"\bqc\s*[:;]"), Words(@"\brd\s*[:;]"),
        Words(@"\braws?\b"), Words(@"\bscan\b"),
    ];

    private static readonly string[] OfficialPhrases =
    [
        "allrightsreserved", "publishedby", "firstpublished", "printedin", "vizmedia", "kodansha", "sevenseas", "squareenix", "yenpress", "copyright", "©",
        "translationrights", "localizationproduced", "authorizedforpublishing", "originalnovelby", "libraryofcongress", "readingdirections", "digitaledition", "ghostship",
    ];

    private static readonly Regex[] OfficialWords = [Words(@"\bisbn\b")];

    /// <summary>
    /// Whether <paramref name="text"/> reads as a scanlator's page: a strong sign with at most one
    /// publisher marker beside it, or a weak sign with none. A bare web address is a weak sign only
    /// when <paramref name="colour"/> (the colour rule also takes the page for a scanlator's), because
    /// Japanese colophons print one too, in black and white.
    /// </summary>
    public static bool IsScanlator(string text, bool colour)
    {
        var spaced = text.ToLowerInvariant();
        var squashed = Squash().Replace(spaced, "");

        // An official staff list names a typesetter too, so it weighs as two markers: no scanlator sign beside it counts.
        var official = Count(OfficialPhrases, OfficialWords, spaced, squashed) + (StaffBlock().IsMatch(spaced) ? 2 : 0);
        if (official <= 1 && Count(StrongPhrases, StrongWords, spaced, squashed) > 0)
        {
            return true;
        }

        if (official > 0)
        {
            return false;
        }

        return Count(WeakPhrases, WeakWords, spaced, squashed) > 0 || (colour && Domain().IsMatch(spaced));
    }

    private static int Count(string[] phrases, Regex[] words, string spaced, string squashed) =>
        phrases.Count(p => squashed.Contains(p, StringComparison.Ordinal)) + words.Count(w => w.IsMatch(spaced));

    private static Regex Words(string pattern) => new(pattern, RegexOptions.CultureInvariant);

    [GeneratedRegex(@"[\s|]+")]
    private static partial Regex Squash();

    /// <summary>
    /// The production staff list at the end of an official webtoon episode: a line reading "STAFF", then
    /// "Original:" or "Adaptation:" labels (OCR reads them as "Originol", "Adoptotion" and the like).
    /// </summary>
    [GeneratedRegex(@"(?:^|\|)\s*staff\s*\|.*\b(?:origin[a-z]{0,3}|ad[ao]pt[a-z]{2,6})\s*[:.;]", RegexOptions.CultureInvariant)]
    private static partial Regex StaffBlock();

    [GeneratedRegex(@"\.(com|net|org|gg|xyz|to|co|io|me|moe)\b", RegexOptions.CultureInvariant)]
    private static partial Regex Domain();
}
