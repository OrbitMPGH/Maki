using System.Text;
using System.Text.RegularExpressions;

namespace Maki.Core.Parsing;

public enum ChapterAccess
{
    Free,
    Locked,
    Preview
}

public static partial class ChapterAccessReader
{
    // "(1)": how some sources list a free sample volume, with the number alone. Fullwidth brackets and
    // digits are already folded by the time this runs.
    [GeneratedRegex(@"^\s*\(\s*[0-9]+\s*\)\s*$")]
    private static partial Regex BareNumberInBrackets();

    // "【期間限定 無料お試し版 閲覧期限2026年11月4日】": a free trial of a volume that expires on the date given.
    // Only the trial wording counts: 期間限定 alone also marks a limited-time free run of whole chapters.
    private static bool IsTrial(string label) => label.Contains("お試し", StringComparison.Ordinal);

    /// <summary>
    /// What a Mihon extension's chapter label says about reading it: a lock or yen mark in front means
    /// it needs a purchase or login, and "(Preview)" or "試し読み" means a few sample pages. So does a label
    /// that is only a number in brackets, "(1)", and a trial edition ("無料お試し版").
    /// None of them can be downloaded as the chapter, so listing them only adds chapters that can never be fetched.
    /// </summary>
    public static ChapterAccess Of(string? label)
    {
        if (string.IsNullOrEmpty(label))
        {
            return ChapterAccess.Free;
        }

        // Folds "（Preview）" to "(Preview)" and "（１）" to "(1)".
        label = label.Normalize(NormalizationForm.FormKC);
        if (label.Contains("(Preview)", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("試し読み", StringComparison.Ordinal) ||
            IsTrial(label) ||
            BareNumberInBrackets().IsMatch(label))
        {
            return ChapterAccess.Preview;
        }

        return label.Contains("\U0001F512") || label.Contains("\U0001F4B4") ? ChapterAccess.Locked : ChapterAccess.Free;
    }
}
