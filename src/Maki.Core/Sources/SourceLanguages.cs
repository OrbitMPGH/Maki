namespace Maki.Core.Sources;

/// <summary>
/// Reads the <c>languageFilter</c> that every <see cref="ISource.ListChaptersAsync"/> takes, and
/// that <c>SourceMapping.LanguageFilter</c> stores.
/// <para>
/// The value is an ordered comma-separated list of codes ("en,es"). It used to be a single code,
/// and a single code is still what a stored filter usually holds — parsing rather than splitting at
/// every call site is what keeps the two readings from drifting apart across the sources that
/// honour it.
/// </para>
/// </summary>
public static class SourceLanguages
{
    /// <summary>What a source lists when the mapping names no language at all.</summary>
    public const string Default = "en";

    /// <summary>
    /// The codes a filter asks for, lowercased and de-duplicated in the order given. An unset or
    /// blank filter means <see cref="Default"/> rather than "every language": a mapping that has
    /// never been touched must keep listing exactly the English chapters it listed before, or every
    /// existing series would suddenly grow a chapter row per translation.
    /// </summary>
    public static IReadOnlyList<string> Parse(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return [Default];
        }

        var codes = new List<string>();
        foreach (var raw in filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var code = raw.ToLowerInvariant();
            if (!codes.Contains(code, StringComparer.Ordinal))
            {
                codes.Add(code);
            }
        }

        return codes.Count > 0 ? codes : [Default];
    }

    /// <summary>
    /// The one spelling a chapter's language is stored under, whichever source tagged it. Sources
    /// write the same language differently ("pt-BR" and "pt-br", MangaDex's plain "zh" for what
    /// Baozi tags "zh-Hans"), and the tag is half of chapter identity, so a series linked to two of
    /// them would otherwise get every chapter twice. A code Maki ships a catalogue for takes the
    /// shipped spelling (<see cref="Localization.SupportedLanguages.All"/>), plain "zh" is the
    /// simplified script, and anything else is lowercased with a four-letter script subtag
    /// capitalised ("zh-Hant"), which is how Webtoons already spells it.
    /// </summary>
    public static string Canonical(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return string.Empty;
        }

        var trimmed = code.Trim();
        if (trimmed.Equals("zh", StringComparison.OrdinalIgnoreCase))
        {
            return "zh-Hans";
        }

        var shipped = Localization.SupportedLanguages.All
            .FirstOrDefault(c => c.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
        if (shipped is not null)
        {
            return shipped;
        }

        var parts = trimmed.ToLowerInvariant().Split('-');
        for (var i = 1; i < parts.Length; i++)
        {
            if (parts[i].Length == 4 && parts[i].All(char.IsAsciiLetter))
            {
                parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i][1..];
            }
        }

        return string.Join('-', parts);
    }

    /// <summary>Whether two chapter languages are the same language, however each was spelled.</summary>
    public static bool Same(string? a, string? b) =>
        string.Equals(a, b, StringComparison.Ordinal) ||
        string.Equals(Canonical(a), Canonical(b), StringComparison.Ordinal);

    /// <summary>The stored form of <paramref name="codes"/>, or null when it is just the default.</summary>
    public static string? Serialize(IReadOnlyList<string> codes) =>
        codes.Count == 1 && codes[0].Equals(Default, StringComparison.OrdinalIgnoreCase)
            ? null
            : string.Join(',', codes);

    /// <summary>
    /// Whether <paramref name="language"/> is one of <paramref name="wanted"/>. Case-insensitive,
    /// and a bare code accepts a regional one ("pt" accepts MangaFire's "pt-br") so a filter written
    /// for one site still means something on another that spells its codes more precisely.
    /// </summary>
    public static bool Includes(IReadOnlyList<string> wanted, string? language) =>
        !string.IsNullOrWhiteSpace(language) &&
        wanted.Any(w => Entities.LocalizedTitle.Matches(language, w));
}
