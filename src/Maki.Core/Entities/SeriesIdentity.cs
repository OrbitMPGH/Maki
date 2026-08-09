using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Maki.Core.Entities;

/// <summary>
/// A series identity that outlives the series row.
/// <para>
/// <see cref="StatsEvent.SeriesId"/> is severed to NULL when a series is deleted, so nothing in the
/// activity log tied a removed series to the same series added back later — the two halves of one
/// history aggregated as two entries, and the older half lost its cover and its link. This key is
/// written onto the event at write time and never touched again, so both halves group together.
/// </para>
/// <para>
/// Provider ids come first because they survive a rename, which a title key does not. The title
/// fallback is exact-match only, deliberately: wrongly splitting one series into two is a visible
/// annoyance somebody can report, while wrongly merging two series silently corrupts both sets of
/// numbers and there is no undo.
/// </para>
/// </summary>
public static partial class SeriesIdentity
{
    /// <summary>
    /// The strongest key available for a live series. Never null — the title fallback always
    /// produces something, even for a series with no provider ids at all.
    /// </summary>
    public static string For(Series series) =>
        series.MangaBakaId is int mb ? $"mb:{mb}"
        : !string.IsNullOrWhiteSpace(series.MangaDexUuid) ? $"md:{series.MangaDexUuid}"
        : series.AniListId is int al ? $"al:{al}"
        : series.MalId is int mal ? $"mal:{mal}"
        : ForTitle(series.Title);

    /// <summary>
    /// The title-only key. Also computed for a series that <em>does</em> have provider ids, as the
    /// secondary key adoption falls back on: rows written before this column existed only ever got
    /// a title key, and a series imported from disk may have had no ids when it was first read.
    /// </summary>
    public static string ForTitle(string title) => $"t:{NormalizeTitle(title)}";

    /// <summary>
    /// Casefold, strip accents and punctuation, collapse whitespace. Same shape as
    /// <c>ScrobbleMatching.NormalizeTitle</c>, duplicated here rather than referenced because that
    /// one is free to change its rules for tracker matching — this one cannot, ever: it is
    /// persisted, and changing it would orphan every key already written.
    /// </summary>
    public static string NormalizeTitle(string title)
    {
        var decomposed = title.Normalize(NormalizationForm.FormD);
        var stripped = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                stripped.Append(ch);
            }
        }

        var t = stripped.ToString().Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        t = NonWord().Replace(t, " ");
        return Whitespace().Replace(t, " ").Trim();
    }

    [GeneratedRegex(@"[^\w\s]")]
    private static partial Regex NonWord();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
