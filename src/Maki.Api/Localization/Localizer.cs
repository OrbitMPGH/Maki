using System.Globalization;
using Jeffijoe.MessageFormat;
using Maki.Core.Localization;

namespace Maki.Api.Localization;

/// <summary>
/// <see cref="ILocalizer"/> over the embedded <c>server.po</c> catalogues.
/// <para>
/// Scoped, because <see cref="Get"/> answers in the current request's language and
/// <see cref="IRequestLocale"/> is scoped. The catalogues themselves and the ICU formatter are
/// singletons behind it; nothing per-request is parsed.
/// </para>
/// </summary>
public sealed class Localizer(
    ServerCatalogs catalogs,
    IMessageFormatter formatter,
    IRequestLocale requestLocale,
    ILogger<Localizer> logger) : ILocalizer
{
    public string Get(string key, object? args = null) => GetFor(requestLocale.Locale, key, args);

    public string GetFor(string locale, string key, object? args = null)
    {
        var resolved = SupportedLanguages.Resolve(locale);

        // English is the source, so it is the last stop rather than one option among fourteen. A key
        // missing there too is a bug in the catalogue, not a missing translation.
        var pattern = catalogs.Lookup(resolved, key)
                      ?? catalogs.Lookup(SupportedLanguages.Default, key);

        if (pattern is null)
        {
            // Deliberately not an exception. Most of these calls are on the way to reporting some
            // other failure to a user, and throwing there replaces a wrong message with a 500.
            // LocalizationCatalogTests is what actually catches this, at build time.
            logger.LogWarning("No message for key {Key} in {Locale} or English", key, resolved);
            return key;
        }

        if (args is null) return pattern;

        try
        {
            // The culture is passed per call rather than taken from the thread. Nothing in this app
            // sets CurrentCulture, and nothing should: about thirty call sites parse chapter numbers
            // and file sizes with InvariantCulture on purpose, and an ambient German or Turkish
            // culture would silently reinterpret "12.5".
            // A dictionary has to take the interface's own overload. Handing it to the object one
            // reflects over Dictionary's own properties (Count, Keys, ...) and finds no arguments at
            // all, which formats as a message with every placeholder missing rather than as an error.
            return args is IReadOnlyDictionary<string, object?> map
                ? formatter.FormatMessage(pattern, map!, CultureInfo.GetCultureInfo(resolved))
                : formatter.FormatMessage(pattern, args, CultureInfo.GetCultureInfo(resolved));
        }
        catch (Exception ex)
        {
            // A translator can write a broken ICU block, and it must not take an endpoint down.
            // Serving the unformatted pattern at least names the problem on screen.
            logger.LogError(ex, "Message {Key} in {Locale} failed to format", key, resolved);
            return pattern;
        }
    }
}
