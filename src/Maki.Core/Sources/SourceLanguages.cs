namespace Maki.Core.Sources;

/// <summary>
/// Reads the <c>languageFilter</c> that every <see cref="ISource.ListChaptersAsync"/> takes, and
/// that <c>SourceMapping.LanguageFilter</c> stores.
/// <para>
/// The value is an ordered comma-separated list of codes ("en,es"). It used to be a single code,
/// and a single code is still what a stored filter usually holds — parsing rather than splitting at
/// every call site is what keeps the two readings from drifting apart across the three sources that
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
