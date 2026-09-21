namespace Maki.Api.Localization;

/// <summary>
/// Looks a message key up in the server message catalogue and formats it.
/// <para>
/// Keys are explicit dotted names ("error.series.notFound"), not English sentences, because a
/// <c>.cs</c> file has to be able to name one. That is the opposite of the client catalogue, where
/// <c>lingui extract</c> generates entries keyed by their own English text and nothing outside the
/// frontend ever refers to them.
/// </para>
/// <para>
/// Messages are ICU, so a placeholder is <c>{name}</c> and a plural is a <c>{n, plural, ...}</c>
/// block. Same syntax as the client catalogue, which is the point: a string can move between the two
/// without being rewritten, and there is one thing for a translator to learn.
/// </para>
/// </summary>
public interface ILocalizer
{
    /// <summary>
    /// The message for <paramref name="key"/> in the current request's language.
    /// <para>
    /// <paramref name="args"/> is an anonymous object whose properties fill the ICU placeholders:
    /// <c>Get("error.settings.urlInvalid", new { service = "Kavita" })</c>.
    /// </para>
    /// <para>
    /// Never throws for a missing key. An unknown one falls back to English and then to the key
    /// itself, because a controller returning "error.series.notFound" to a user is bad and a
    /// controller throwing on the way to reporting another error is worse.
    /// </para>
    /// </summary>
    string Get(string key, object? args = null);

    /// <summary>
    /// Same, in an explicitly named language, for code with no request behind it: a Quartz job, a
    /// webhook dispatch, anything resolving a recipient's own preference through
    /// <see cref="IUserLocaleResolver"/>.
    /// <para>
    /// Deliberately a separate method rather than an optional first parameter. Overloading on
    /// <c>(string, object?)</c> against <c>(string, string, object?)</c> would make
    /// <c>Get("a", "b")</c> compile as either one.
    /// </para>
    /// </summary>
    string GetFor(string locale, string key, object? args = null);
}
