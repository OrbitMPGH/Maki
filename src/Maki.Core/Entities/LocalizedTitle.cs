namespace Maki.Core.Entities;

/// <summary>
/// A series title plus the language it is written in. A null <see cref="Language"/> means the
/// provider carried the title but not a code for it — the title is still usable, it just can't be
/// selected by a language preference.
/// </summary>
/// <param name="Language">
/// Lowercase code as the provider spelled it. Mostly ISO 639-1 ("en", "ja"), sometimes regional
/// ("pt-br", "es-la"), which is why <see cref="Matches"/> compares the part before the hyphen too.
/// </param>
public record LocalizedTitle(string Title, string? Language)
{
    /// <summary>
    /// First title whose language matches one of <paramref name="preferred"/>, in preference order
    /// rather than list order — "ja,en" picks the Japanese title even when the English one comes
    /// first. Null when nothing matches.
    /// </summary>
    public static string? Pick(IEnumerable<LocalizedTitle> titles, IReadOnlyList<string> preferred)
    {
        var candidates = titles as IReadOnlyCollection<LocalizedTitle> ?? [.. titles];
        foreach (var want in preferred)
        {
            var hit = candidates.FirstOrDefault(t => Matches(t.Language, want));
            if (hit is not null)
            {
                return hit.Title;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="language"/> satisfies a preference for <paramref name="wanted"/>.
    /// Case-insensitive, and a bare code matches a <em>regional</em> variant of itself: "pt" finds
    /// MangaFire's "pt-br", "es" finds "es-la". Not the other way round — asking for "pt-br" and
    /// getting the European edition is a different translation than the one chosen.
    /// <para>
    /// A <em>script</em> variant deliberately does not match: MangaBaka tags romanizations
    /// "ja-Latn", and someone who asked for Japanese titles wants カッコいい女の子, not
    /// "Kakkoi Onnanoko". BCP-47 tells the two apart by length — a script subtag is four letters,
    /// a region subtag two letters or three digits.
    /// </para>
    /// </summary>
    public static bool Matches(string? language, string? wanted)
    {
        if (string.IsNullOrWhiteSpace(language) || string.IsNullOrWhiteSpace(wanted))
        {
            return false;
        }

        if (language.Equals(wanted, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (wanted.IndexOf('-') >= 0 ||
            language.Length <= wanted.Length ||
            language[wanted.Length] != '-' ||
            !language.AsSpan(0, wanted.Length).Equals(wanted, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return language.Length - wanted.Length - 1 != 4;
    }

    /// <summary>Splits the stored "ja,en" preference into codes. Empty when unset.</summary>
    public static IReadOnlyList<string> ParsePreference(string? setting) =>
        string.IsNullOrWhiteSpace(setting)
            ? []
            : [.. setting.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
