using System.Globalization;
using System.Text.RegularExpressions;

namespace Maki.Core.Parsing;

/// <summary>
/// Reads scanlation/torrent conventions out of <see cref="ParsedReleaseFile.Tags"/>: which one
/// says "Digital", which one is a year, which one names the release group. <see cref="Group"/> is
/// deliberately conservative: it only rules out tags that are clearly something else (a year, a
/// resolution, a bare letter like the "(f)" flag, a version/volume/chapter marker like "(v2)" or
/// "(c05)", a Maki language-suffix tag like "es" or "pt-br", or a small set of known noise words);
/// anything left, including a publisher name like "Kobo" or "Yen Press", is assumed to identify
/// provenance and returned as the group. Misreading a noise tag as a group is worse than missing
/// a real one, since it mislabels where the file came from.
/// </summary>
public static partial class ReleaseTags
{
    [GeneratedRegex(@"^([0-9]{4})(?:-[0-9]{4})?$")]
    private static partial Regex YearPattern();

    [GeneratedRegex(@"^[0-9]+x[0-9]+$", RegexOptions.IgnoreCase)]
    private static partial Regex ResolutionPattern();

    [GeneratedRegex(@"^(?:v|vol\.?|volumes?|c|ch\.?|chapters?)\s*[0-9]+(?:[.-](?:v|c|ch\.?)?[0-9]+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionMarkerPattern();

    // Two lowercase letters, optionally with a region ("pt-BR", "pt-br") or script ("zh-Hans",
    // "zh-hans") suffix, are Maki's own language-suffix tags, in either the canonical or the older
    // all-lowercase spelling. Case-sensitive on purpose: an uppercase or capitalised group name
    // like "TCB" or "Ao" doesn't match and still survives as a group, and no source declares a
    // three-letter code, so "lfp" is a group.
    [GeneratedRegex(@"^[a-z]{2}(?:-(?:[a-z]{2,4}|[A-Z]{2}|[A-Z][a-z]{3}))?$")]
    private static partial Regex LanguageTagPattern();

    // These describe the release, not who made it. The uppercase language codes and "END"/"Fin" are
    // matched exactly: ignoring case would also reject real groups spelled "End", "Fin" or "Tr".
    private static readonly HashSet<string> CaseSensitiveNoiseTokens = new(StringComparer.Ordinal)
    {
        "END", "Fin",
        "DE", "FR", "ES", "PT", "PT-BR", "BR", "PL", "RU", "TR", "JP", "KR", "CN"
    };

    // The three-letter codes are ISO 639-2 language tags, which the two-letter pattern above
    // deliberately leaves alone. The language names cover what non-English releases write
    // ("[Español]"), and the container formats are what a scene release puts in brackets ("[CBZ]");
    // none of them names a group.
    private static readonly HashSet<string> NoiseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "CBZ", "CBR", "CB7", "PDF", "EPUB",
        "German", "Deutsch", "French", "Français", "Francais", "VF", "VOSTFR", "Spanish", "Español",
        "Espanol", "Portuguese", "Português", "Portugues", "Polish", "Polski", "Russian", "Русский",
        "Turkish", "Türkçe", "Turkce", "Japanese", "日本語", "Korean", "한국어", "Chinese", "中文", "简体",
        "BW", "c2c", "Censored", "Chapter", "Chapters", "Color", "Colored", "Colour", "Coloured",
        "Compilation", "compilation", "Complete", "Completed", "Digital", "Digital-HD",
        "Digital-Compilation", "English", "Fan", "Fanmade", "Fixed", "HD", "Manga", "Manhua",
        "Manhwa", "Official", "Omnibus", "One-shot", "Oneshot", "Ongoing", "Raw", "Raws", "rip",
        "Remastered", "Scan", "scanned", "Scanlation", "Tankobon", "Tankoubon", "Uncensored",
        "Volume", "Volumes", "web", "web-dl", "webdl", "webrip", "Webtoon",
        "ara", "chi", "deu", "eng", "fra", "fre", "ger", "ind", "ita", "jp", "jpn", "kor", "pol",
        "por", "rus", "spa", "tha", "tur", "vie", "zh", "zho"
    };

    public static bool IsDigital(IReadOnlyList<string> tags) =>
        tags.Any(t =>
            string.Equals(t, "Digital", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("Digital-", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("Digital ", StringComparison.OrdinalIgnoreCase));

    /// <summary>The first 4-digit year, or the start year of a range like "2023-2025".</summary>
    public static int? Year(IReadOnlyList<string> tags)
    {
        foreach (var tag in tags)
        {
            var match = YearPattern().Match(tag);
            if (match.Success) return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        return null;
    }

    /// <summary>The first tag that names a release group or publisher, or null if none does.</summary>
    public static string? Group(IReadOnlyList<string> tags)
    {
        foreach (var tag in tags)
        {
            if (YearPattern().IsMatch(tag)) continue;
            if (ResolutionPattern().IsMatch(tag)) continue;
            if (tag.Length == 1 && char.IsLetter(tag[0])) continue;
            if (VersionMarkerPattern().IsMatch(tag)) continue;
            if (IsMarker(tag)) continue;
            if (tag.StartsWith("Digital", StringComparison.OrdinalIgnoreCase)) continue;
            if (LanguageTagPattern().IsMatch(tag)) continue;
            if (NoiseTokens.Contains(tag) || CaseSensitiveNoiseTokens.Contains(tag)) continue;
            return tag;
        }

        return null;
    }

    /// <summary>
    /// A tag that is nothing but a volume or chapter marker in any language ("(Tome 3)", "(第3巻)").
    /// Every language counts whatever the settings say: a marker is never a group's name.
    /// </summary>
    private static bool IsMarker(string tag) =>
        NameParsingProfile.AllLanguages.VolumeMatches(tag)
            .Concat(NameParsingProfile.AllLanguages.ChapterMatches(tag))
            .Any(m => m.Index == 0 && m.Length == tag.Length);
}
