using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Maki.Sources.Common;

/// <summary>
/// Reads the number out of a "第N话/話/回" chapter label or a "第N卷/巻" volume label. Labels are
/// NFKC-normalised first so fullwidth digits (第１２话) read as ASCII, and only ASCII digits are
/// matched, so a numeral the parse methods would reject can never reach them.
/// </summary>
internal static partial class CjkChapterNumber
{
    [GeneratedRegex(@"第?\s*([0-9]+(?:\.[0-9]+)?)\s*[话話回]")]
    private static partial Regex ChapterMarkerRegex();

    [GeneratedRegex(@"第?\s*([0-9]+)\s*[卷巻]")]
    private static partial Regex VolumeMarkerRegex();

    public static decimal? Chapter(string label)
    {
        var match = ChapterMarkerRegex().Match(label.Normalize(NormalizationForm.FormKC));
        return match.Success
               && decimal.TryParse(match.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
    }

    public static int? Volume(string label)
    {
        var match = VolumeMarkerRegex().Match(label.Normalize(NormalizationForm.FormKC));
        return match.Success
               && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var volume)
            ? volume
            : null;
    }
}
