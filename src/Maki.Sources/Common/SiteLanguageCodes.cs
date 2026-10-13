using Maki.Core.Sources;

namespace Maki.Sources.Common;

/// <summary>
/// A stored <c>LanguageFilter</c> as the multi-language sites spell it.
/// <para>
/// The filter is seeded from Maki's own UI locales (<c>SourceLanguagePreference.SeedFilter</c>)
/// and MangaDex and MangaFire tag chapters with plain ISO 639-1, so the two agree on thirteen of the
/// fourteen by luck rather than by design. Simplified Chinese is the exception: Maki writes
/// <c>zh-Hans</c>, both sites file it under <c>zh</c>, and a request for <c>zh-hans</c> matches
/// nothing at all, so the mapping listed zero chapters while every screen reported it enabled and
/// matched.
/// </para>
/// <para>
/// Anything not named here is passed through as it was stored. An unknown code is answered with an
/// empty list rather than an error either way, and inventing a translation for a code no picker can
/// produce would only hide the next mismatch.
/// </para>
/// </summary>
internal static class SiteLanguageCodes
{
    public static IReadOnlyList<string> Parse(string? filter) =>
        SourceLanguages.Parse(filter).Select(ToSite).Distinct().ToList();

    public static string ToSite(string code) => code switch
    {
        "zh-hans" => "zh",
        _ => code,
    };
}
