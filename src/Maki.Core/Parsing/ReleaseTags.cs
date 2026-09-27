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
    [GeneratedRegex(@"^(\d{4})(?:-\d{4})?$")]
    private static partial Regex YearPattern();

    [GeneratedRegex(@"^\d+x\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex ResolutionPattern();

    [GeneratedRegex(@"^(?:v|vol\.?|volumes?|c|ch\.?|chapters?)\s*\d+(?:[.-](?:v|c|ch\.?)?\d+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionMarkerPattern();

    // Two or three lowercase letters, optionally with a lowercase region suffix, are Maki's own
    // language-suffix tags ("es", "pt-br", "zh-hans"). Case-sensitive on purpose: an uppercase or
    // capitalised group name like "TCB" or "Ao" doesn't match and still survives as a group.
    [GeneratedRegex(@"^[a-z]{2,3}(?:-[a-z]{2,4})?$")]
    private static partial Regex LanguageTagPattern();

    // These describe the release, not who made it.
    private static readonly HashSet<string> NoiseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "BW", "c2c", "Censored", "Chapter", "Chapters", "Color", "Colored", "Colour", "Coloured",
        "Compilation", "compilation", "Complete", "Completed", "Digital", "Digital-HD",
        "Digital-Compilation", "English", "Fan", "Fanmade", "Fixed", "HD", "Manga", "Manhua",
        "Manhwa", "Official", "Omnibus", "One-shot", "Oneshot", "Ongoing", "Raw", "Raws", "rip",
        "Remastered", "Scan", "scanned", "Scanlation", "Tankobon", "Tankoubon", "Uncensored",
        "Volume", "Volumes", "web", "web-dl", "webdl", "webrip", "Webtoon"
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
            if (tag.StartsWith("Digital", StringComparison.OrdinalIgnoreCase)) continue;
            if (LanguageTagPattern().IsMatch(tag)) continue;
            if (NoiseTokens.Contains(tag)) continue;
            return tag;
        }

        return null;
    }
}
