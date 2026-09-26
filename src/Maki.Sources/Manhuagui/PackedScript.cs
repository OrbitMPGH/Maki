using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Maki.Sources.Manhuagui;

/// <summary>
/// Unpacks manhuagui's Dean Edwards "packer" output without a JS engine. A chapter page embeds
/// <c>window["\x65\x76\x61\x6c"](function(p,a,c,k,e,d){...}('...',a,c,'...'.split('|'),0,{}))</c> —
/// <c>eval</c> of a templated call whose body <c>p</c> has every long identifier replaced by a short
/// base-<c>a</c> token, and whose word list <c>k</c> is lz-string-compressed and <c>|</c>-joined.
/// <c>c</c> (the word count) isn't needed: it's implied by the split word list's length.
/// </summary>
public static partial class PackedScript
{
    [GeneratedRegex(@"\}\('(?<p>.*)',(?<a>\d+),(?<c>\d+),'(?<k>[A-Za-z0-9+/=]+)'\[", RegexOptions.Singleline)]
    private static partial Regex PackedCallRegex();

    [GeneratedRegex(@"\{.*\}", RegexOptions.Singleline)]
    private static partial Regex JsonBodyRegex();

    [GeneratedRegex(@"\b\w+\b")]
    private static partial Regex TokenRegex();

    /// <summary>
    /// Parses the packed call out of a chapter page's HTML and returns the page data it decodes to.
    /// Throws <see cref="InvalidDataException"/> if the packer call or its JSON body isn't found —
    /// a layout change, never a locked or empty chapter (that comes back as zero <c>Files</c>).
    /// </summary>
    public static ManhuaguiChapterData Unpack(string html)
    {
        var match = PackedCallRegex().Match(html);
        if (!match.Success)
        {
            throw new InvalidDataException("manhuagui: packed script layout changed, no match for the packer call");
        }

        // The packer escapes single quotes inside its own string literal; undo that before
        // the string is used as a plain JS-ish template.
        var template = match.Groups["p"].Value.Replace("\\'", "'");
        var baseN = int.Parse(match.Groups["a"].Value, CultureInfo.InvariantCulture);
        var words = LzString.DecompressFromBase64(match.Groups["k"].Value).Split('|');

        var unpacked = TokenRegex().Replace(template, m => Substitute(m.Value, baseN, words));

        var jsonMatch = JsonBodyRegex().Match(unpacked);
        if (!jsonMatch.Success)
        {
            throw new InvalidDataException("manhuagui: unpacked script has no JSON body");
        }

        return JsonSerializer.Deserialize<ManhuaguiChapterData>(jsonMatch.Value)
            ?? throw new InvalidDataException("manhuagui: chapter JSON deserialized to null");
    }

    /// <summary>
    /// Inverts the packer's <c>e(c) = (c &lt; a ? "" : e(c / a)) + (c % a &gt; 35 ? String.fromCharCode(c % a + 29) : c.toString(36))</c>:
    /// reads <paramref name="token"/> as a base-<paramref name="baseN"/> number (not a fixed base 62 —
    /// each digit's value comes from where it sits in "0-9a-zA-Z") and looks it up in <paramref name="words"/>.
    /// A token whose value is out of range or maps to an empty word is left as-is; that's the packer's
    /// own signal that the token was never one of its replaced identifiers.
    /// </summary>
    private static string Substitute(string token, int baseN, string[] words)
    {
        var value = 0;
        foreach (var ch in token)
        {
            var digit = BaseDigit(ch);
            if (digit < 0)
            {
                return token;
            }

            value = value * baseN + digit;
        }

        return value < words.Length && words[value].Length > 0 ? words[value] : token;
    }

    private static int BaseDigit(char ch) => ch switch
    {
        >= '0' and <= '9' => ch - '0',
        >= 'a' and <= 'z' => ch - 'a' + 10,
        >= 'A' and <= 'Z' => ch - 'A' + 36,
        _ => -1
    };
}

/// <summary>The <c>SMH.imgData({...})</c> payload a manhuagui chapter page's packed script decodes to.</summary>
public record ManhuaguiChapterData(
    [property: JsonPropertyName("bid")] int Bid,
    [property: JsonPropertyName("bname")] string? Bname,
    [property: JsonPropertyName("cid")] int Cid,
    [property: JsonPropertyName("cname")] string? Cname,
    [property: JsonPropertyName("files")] List<string> Files,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("sl")] ManhuaguiSl Sl);

/// <summary>The CDN signature: <see cref="E"/> is an expiry epoch, <see cref="M"/> a per-chapter MAC.</summary>
public record ManhuaguiSl(
    [property: JsonPropertyName("e")] long E,
    [property: JsonPropertyName("m")] string M);
