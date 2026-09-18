namespace Maki.Api.Localization;

/// <summary>
/// Which language to answer this request in.
/// <para>
/// Deliberately <em>not</em> <c>CultureInfo.CurrentUICulture</c>, and this app deliberately never
/// calls <c>UseRequestLocalization</c>. That middleware sets <c>CurrentCulture</c> alongside
/// <c>CurrentUICulture</c>, which changes <c>decimal.Parse</c>, numeric <c>ToString</c> and date
/// formatting for everything on the thread. About thirty places here use
/// <c>CultureInfo.InvariantCulture</c> on purpose, to parse chapter numbers, file sizes and dates,
/// and an ambient German or Turkish culture silently reinterpreting "12.5" as a chapter number is a
/// bug that would not fail a build and would reach the filesystem.
/// </para>
/// <para>
/// So the language travels as ordinary scoped state that only the localizer reads, and ambient
/// culture is left alone.
/// </para>
/// </summary>
public interface IRequestLocale
{
    /// <summary>
    /// A supported language code, never null and never unsupported. Resolved once per request on
    /// first read, because most requests return pure data and never need it.
    /// </summary>
    string Locale { get; }
}
