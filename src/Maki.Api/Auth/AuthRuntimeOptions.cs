using Maki.Core.Configuration;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Auth;

/// <summary>
/// The <c>auth.*</c> settings, read once at startup into a singleton.
/// <para>
/// Read at startup rather than per request because these values configure things the options system
/// and the middleware pipeline both build exactly once: the cookie's <c>Secure</c> policy, HSTS and
/// HTTPS redirection, which proxies are trusted, the lockout thresholds. Changing any of them takes
/// effect on restart, and the settings UI says so — the alternative is a pipeline that reconfigures
/// itself mid-flight, which is a great deal of machinery for a setting touched once per deployment.
/// </para>
/// <para>
/// Loaded in one query rather than through <see cref="IAppSettings"/>, which opens a fresh scope and
/// DbContext per key — the same reason <c>OpdsAccessService</c> exists.
/// </para>
/// </summary>
public class AuthRuntimeOptions
{
    public const int DefaultLockoutMaxAttempts = 5;
    public const int DefaultLockoutMinutes = 15;
    public const int DefaultSessionDays = 30;

    /// <summary>
    /// Redirect to HTTPS, send HSTS, and require <c>Secure</c> on the session cookie.
    /// <para>
    /// Off by default and that default is deliberate: the common deployment is plain HTTP on a LAN,
    /// where a <c>Secure</c> cookie is set by the server and then never sent back by the browser —
    /// producing a login that silently fails with nothing in any log to explain it.
    /// </para>
    /// </summary>
    public bool RequireHttps { get; private set; }

    /// <summary>
    /// Proxy addresses or CIDR networks permitted to set <c>X-Forwarded-*</c>. Empty means forwarded
    /// headers are ignored: honouring them from anyone lets a client claim any source address, which
    /// both forges the audit log and sidesteps per-IP rate limiting.
    /// </summary>
    public IReadOnlyList<string> TrustedProxies { get; private set; } = [];

    /// <summary>Failed attempts before lockout. Zero disables lockout entirely.</summary>
    public int LockoutMaxAttempts { get; private set; } = DefaultLockoutMaxAttempts;

    public TimeSpan LockoutDuration { get; private set; } = TimeSpan.FromMinutes(DefaultLockoutMinutes);

    public TimeSpan SessionLifetime { get; private set; } = TimeSpan.FromDays(DefaultSessionDays);

    public async Task LoadAsync(MakiDbContext db, CancellationToken ct = default)
    {
        var rows = await db.AppConfig
            .AsNoTracking()
            .Where(c => c.Key == SettingKeys.AuthRequireHttps
                || c.Key == SettingKeys.AuthTrustedProxies
                || c.Key == SettingKeys.AuthLockoutMaxAttempts
                || c.Key == SettingKeys.AuthLockoutMinutes
                || c.Key == SettingKeys.AuthSessionDays)
            .ToDictionaryAsync(c => c.Key, c => c.Value, ct);

        RequireHttps = rows.GetValueOrDefault(SettingKeys.AuthRequireHttps) == "true";

        TrustedProxies = (rows.GetValueOrDefault(SettingKeys.AuthTrustedProxies) ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        LockoutMaxAttempts = ReadInt(rows, SettingKeys.AuthLockoutMaxAttempts, DefaultLockoutMaxAttempts, min: 0);
        LockoutDuration = TimeSpan.FromMinutes(
            ReadInt(rows, SettingKeys.AuthLockoutMinutes, DefaultLockoutMinutes, min: 1));
        SessionLifetime = TimeSpan.FromDays(
            ReadInt(rows, SettingKeys.AuthSessionDays, DefaultSessionDays, min: 1));
    }

    private static int ReadInt(Dictionary<string, string> rows, string key, int fallback, int min) =>
        int.TryParse(rows.GetValueOrDefault(key), out var value) && value >= min ? value : fallback;
}
