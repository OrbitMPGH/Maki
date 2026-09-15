using Maki.Core.Configuration;
using Maki.Core.Localization;
using Microsoft.Extensions.Caching.Memory;

namespace Maki.Api.Localization;

/// <summary>
/// One user's language, read through <see cref="IUserSettingsStore"/> because the callers have no
/// current user of their own. Singleton, with a short cache: the answer changes only when somebody
/// saves the setting, and <see cref="Forget"/> is called from there.
/// </summary>
public sealed class UserLocaleResolver(
    IUserSettingsStore store,
    IAppSettings settings,
    IMemoryCache cache) : IUserLocaleResolver
{
    // Short enough that a missed Forget heals on its own, long enough that a batch of notifications
    // to one person is a single read.
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private static string Key(int userId) => $"locale:{userId}";

    public async Task<string> ResolveAsync(int userId, CancellationToken ct = default)
    {
        if (cache.TryGetValue(Key(userId), out string? cached) && cached is not null) return cached;

        var stored = await store.GetAsync(userId, SettingKeys.UiLanguage, ct);
        // An unset preference means "follow the browser", which a background job has no way to read,
        // so it falls to the instance default rather than to English directly.
        var locale = SupportedLanguages.Match(stored) ?? await DefaultAsync(ct);

        cache.Set(Key(userId), locale, Ttl);
        return locale;
    }

    public async Task<string> DefaultAsync(CancellationToken ct = default) =>
        SupportedLanguages.Resolve(await settings.GetAsync(SettingKeys.UiDefaultLanguage, ct));

    public void Forget(int userId) => cache.Remove(Key(userId));
}
