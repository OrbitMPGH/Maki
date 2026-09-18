namespace Maki.Api.Localization;

/// <summary>
/// Renders a message in a language the caller names. Singleton, so a singleton can hold one.
/// <para>
/// <see cref="ILocalizer"/> is the surface almost everything should use, but it is scoped: its
/// <see cref="ILocalizer.Get"/> answers in the current request's language, and there is no request
/// language outside a request. A singleton that injected it would capture the first scope it ever
/// saw. This is the half of that interface with no such problem, for the background senders that
/// resolve a locale for themselves: a Quartz job, the download batch notifier, an outbound webhook.
/// </para>
/// </summary>
public interface IMessageCatalog
{
    /// <inheritdoc cref="ILocalizer.GetFor"/>
    string GetFor(string locale, string key, object? args = null);
}
