using System.Globalization;
using System.Net;
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

    // Upper bounds exist because TimeSpan.FromDays throws past about 10.6 million days, which would
    // stop the host from starting, and far smaller values still overflow the cookie's expiry date.
    public const int MaxLockoutMaxAttempts = 1000;
    public const int MaxLockoutMinutes = 10080;
    public const int MaxSessionDays = 3650;

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

        LockoutMaxAttempts = LockoutMaxAttemptsFrom(rows.GetValueOrDefault(SettingKeys.AuthLockoutMaxAttempts));
        LockoutDuration = TimeSpan.FromMinutes(LockoutMinutesFrom(rows.GetValueOrDefault(SettingKeys.AuthLockoutMinutes)));
        SessionLifetime = TimeSpan.FromDays(SessionDaysFrom(rows.GetValueOrDefault(SettingKeys.AuthSessionDays)));

        // Lockout is expressed by the threshold alone (see ApplyLockout), so every account has to
        // carry LockoutEnabled. Accounts created by an older build while the threshold was zero got
        // false and would otherwise never lock out again once it was raised.
        await db.Users
            .Where(u => !u.LockoutEnabled)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.LockoutEnabled, true), ct);
    }

    /// <summary>
    /// Whether a sign-in from this page would hand the browser a <c>Secure</c> session cookie it is
    /// going to throw away, leaving a login that answers 200 and then 401s on every request after.
    /// <para>
    /// Judged from the browser's <c>Origin</c> rather than <c>Request.IsHttps</c>: behind a TLS proxy
    /// that is not in <see cref="TrustedProxies"/> the request reaches Maki as plain HTTP while the
    /// browser is on HTTPS and keeps the cookie just fine. Loopback counts as secure because browsers
    /// accept <c>Secure</c> cookies there. No <c>Origin</c> means nothing to judge by, so no refusal.
    /// </para>
    /// </summary>
    public bool SessionCookieWouldBeDropped(string? origin) =>
        RequireHttps && IsInsecureOrigin(origin);

    /// <summary>
    /// Whether a cookie the app builds by hand should be marked <c>Secure</c>: the request is HTTPS,
    /// or <c>auth.requirehttps</c> is on, which stays true behind a TLS proxy that is not trusted.
    /// </summary>
    public static bool UseSecureCookie(HttpContext context) =>
        context.Request.IsHttps || context.RequestServices.GetService<AuthRuntimeOptions>()?.RequireHttps == true;

    public static bool IsInsecureOrigin(string? origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttp
        && !uri.IsLoopback
        && !uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One <see cref="TrustedProxies"/> entry: a bare address, or a CIDR network whose prefix is a
    /// plain integer that fits the address family. Host bits below the prefix are cleared, since
    /// <see cref="IPNetwork"/> rejects them. Shared by the settings save and startup so an entry
    /// that saves is one that applies.
    /// </summary>
    public static bool TryParseTrustedProxy(string entry, out IPAddress? proxy, out IPNetwork? network)
    {
        proxy = null;
        network = null;

        var slash = entry.IndexOf('/');
        if (slash < 0)
        {
            return TryParseAddress(entry, out proxy);
        }

        if (!TryParseAddress(entry[..slash], out var address) ||
            !int.TryParse(entry[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (prefix > bytes.Length * 8)
        {
            return false;
        }

        for (var bit = prefix; bit < bytes.Length * 8; bit++)
        {
            bytes[bit / 8] &= (byte)~(0x80 >> (bit % 8));
        }

        network = new IPNetwork(new IPAddress(bytes), prefix);
        return true;
    }

    // IPAddress.TryParse takes legacy IPv4 shorthand ("10" is 0.0.0.10, "172.16" is 172.0.0.16), so
    // an IPv4 entry must read back exactly as written or "172.16/12" would quietly mean 172.0.0.0/12.
    private static bool TryParseAddress(string text, out IPAddress? address) =>
        IPAddress.TryParse(text, out address) &&
        (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || address.ToString() == text);

    public static int LockoutMaxAttemptsFrom(string? stored) =>
        ReadInt(stored, DefaultLockoutMaxAttempts, min: 0, max: MaxLockoutMaxAttempts);

    public static int LockoutMinutesFrom(string? stored) =>
        ReadInt(stored, DefaultLockoutMinutes, min: 1, max: MaxLockoutMinutes);

    public static int SessionDaysFrom(string? stored) =>
        ReadInt(stored, DefaultSessionDays, min: 1, max: MaxSessionDays);

    /// <summary>
    /// Below the floor falls back to the default; above the ceiling clamps to it, since a large value
    /// saved by an older build still states an intent (a long session) worth keeping.
    /// </summary>
    private static int ReadInt(string? stored, int fallback, int min, int max) =>
        int.TryParse(stored, out var value) && value >= min ? Math.Min(value, max) : fallback;
}
