using Maki.Core.Configuration;
using Maki.Core.Localization;
using Maki.Core.Security;

namespace Maki.Api.Localization;

/// <summary>
/// The per-request language, populated by <see cref="RequestLocaleMiddleware"/> and read
/// synchronously downstream. Mirrors <c>CurrentUserContext</c>, which is the established shape here
/// for "something the request decided once, read everywhere after".
/// <para>
/// The split between the two halves is deliberate. What the middleware sets is only what can be read
/// off the request itself, which costs nothing: a query parameter, a header. Everything past that
/// needs a settings read, so it stays behind the property and runs at most once, and only for the
/// requests that actually localize something. Most return pure structured data and never do.
/// </para>
/// </summary>
public sealed class RequestLocaleContext(
    ICurrentUser currentUser,
    IUserSettings userSettings,
    IUserLocaleResolver resolver) : IRequestLocale
{
    private string? _chosen;
    private string? _negotiated;
    private string? _resolved;

    /// <summary>
    /// Called by the middleware. <paramref name="chosen"/> is a language somebody asked for on
    /// purpose; <paramref name="negotiated"/> is the best match against Accept-Language. Either may
    /// be null, and they are kept apart because the stored preference sits between them.
    /// </summary>
    public void SetRequested(string? chosen, string? negotiated)
    {
        _chosen = chosen;
        _negotiated = negotiated;
    }

    public string Locale => _resolved ??= Resolve();

    private string Resolve()
    {
        // 1. Asked for explicitly. The most specific thing anybody said about this request.
        if (_chosen is not null) return _chosen;

        // 2. The user's own setting. Above Accept-Language, not below: browsers attach that header
        //    to every request, so ranking it higher would mean a Swede whose browser is in English
        //    never sees the language they chose. Blocking on the read is acceptable because this
        //    runs at most once per request, and only when something is actually being localized.
        if (currentUser.IsAuthenticated)
        {
            var stored = userSettings.GetAsync(SettingKeys.UiLanguage).GetAwaiter().GetResult();
            if (SupportedLanguages.Match(stored) is { } fromSettings) return fromSettings;
        }

        // 3. What the client says it can read. All a third-party OPDS reader or a curl caller sends.
        if (_negotiated is not null) return _negotiated;

        // 4. Whatever the admin chose for requests with nobody behind them.
        return resolver.DefaultAsync().GetAwaiter().GetResult();
    }
}
