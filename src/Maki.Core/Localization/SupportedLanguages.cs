namespace Maki.Core.Localization;

/// <summary>
/// The languages Maki ships a message catalogue for, and the rules for picking one.
/// <para>
/// A <em>closed</em> set, unlike the title-language preference next to it in
/// <c>SettingKeys</c>: that one accepts any code a metadata provider might use, because an unknown
/// one simply matches no title. Here an unknown code means there is no catalogue to load, and every
/// string in the app renders as an internal hash instead. So anything not on this list is refused
/// on write and read back as <see cref="Default"/>.
/// </para>
/// <para>
/// Must stay in step with <c>SUPPORTED_LOCALES</c> in <c>frontend/src/i18n.ts</c> and with the
/// <c>locales</c> array in <c>frontend/lingui.config.js</c>; the three describe the same set for
/// three different consumers.
/// </para>
/// </summary>
public static class SupportedLanguages
{
    public const string Default = "en";

    /// <summary>
    /// Ordered as the picker shows them: English first as the source language, then the rest by
    /// how widely they are spoken among self-hosters rather than alphabetically, since an
    /// alphabetical list in one language is arbitrary in the other thirteen.
    /// </summary>
    public static readonly string[] All =
    [
        "en",
        "sv", "de", "fr", "es", "pt-BR", "it", "nl",
        "pl", "ru", "tr", "ja", "zh-Hans", "ko",
    ];

    /// <summary>
    /// The shipped code matching <paramref name="code"/>, in its canonical casing, or null.
    /// <para>
    /// Falls back to the primary subtag, so a browser asking for <c>de-AT</c> or <c>sv-FI</c> gets
    /// German or Swedish rather than English. <c>pt</c> resolves to <c>pt-BR</c> because it is the
    /// only Portuguese shipped, and a Brazilian interface reads far closer to a European Portuguese
    /// speaker than an English one does.
    /// </para>
    /// </summary>
    public static string? Match(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var wanted = code.Trim();

        var exact = All.FirstOrDefault(c => string.Equals(c, wanted, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        var primary = wanted.Split('-')[0];
        return All.FirstOrDefault(c =>
            string.Equals(c.Split('-')[0], primary, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <see cref="Match"/> with <see cref="Default"/> as the floor, for the paths that must end up
    /// with some catalogue rather than deciding what to do without one.
    /// </summary>
    public static string Resolve(string? code) => Match(code) ?? Default;
}
