using Maki.Core.Configuration;
using Maki.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Auth;

/// <summary>
/// The <c>auth.oidc*</c> settings, read once at startup into a singleton for the same reason
/// <see cref="AuthRuntimeOptions"/> is: the OpenID Connect handler is configured exactly once, and it
/// fetches the provider's discovery document on first use. A change here takes effect on restart, and
/// the settings card says so.
/// </summary>
public class OidcRuntimeOptions
{
    public const string DefaultDisplayName = "Single sign-on";
    public const string DefaultUsernameClaim = "preferred_username";
    public const string DefaultScopes = "profile email";

    /// <summary>
    /// Where the provider sends the browser back to. Registered as the redirect URI at the provider,
    /// so it is a constant rather than something derived — a value that moved between versions would
    /// break every configured instance.
    /// </summary>
    public const string CallbackPath = "/api/v1/auth/oidc/callback";

    /// <summary>
    /// Restores local password sign-in for everyone regardless of <see cref="OidcOnly"/>.
    /// <para>
    /// An environment variable rather than a setting on purpose: the situation it exists for is
    /// "the identity provider is down, or its client secret rotated, and nobody can sign in to
    /// change the setting that would let them". A value only reachable through the very UI that is
    /// locked would be no escape hatch at all.
    /// </para>
    /// </summary>
    public const string BreakGlassVariable = "MAKI_ALLOW_LOCAL_LOGIN";

    public string Authority { get; private set; } = string.Empty;
    public string ClientId { get; private set; } = string.Empty;
    public string ClientSecret { get; private set; } = string.Empty;

    /// <summary>Extra scopes beyond <c>openid</c>, which the handler always requests.</summary>
    public IReadOnlyList<string> Scopes { get; private set; } = [];

    public string DisplayName { get; private set; } = DefaultDisplayName;
    public string UsernameClaim { get; private set; } = DefaultUsernameClaim;

    public bool AutoProvision { get; private set; }

    /// <summary>Claim name and, optionally, the single value that counts. See <see cref="ClaimRule"/>.</summary>
    public ClaimRule? AdminClaim { get; private set; }

    public string PermissionClaim { get; private set; } = string.Empty;

    private bool _enabled;
    private bool _oidcOnly;

    /// <summary>
    /// Whether single sign-on is both switched on and configured well enough to work. A button that
    /// leads only to a discovery-document failure is worse than no button.
    /// </summary>
    public bool Enabled => _enabled && Authority.Length > 0 && ClientId.Length > 0;

    /// <summary>
    /// Local password login is refused for non-admins. False whenever single sign-on is not actually
    /// usable, so switching it on and then breaking the provider config cannot lock anyone out, and
    /// false whenever the break-glass variable is set.
    /// </summary>
    public bool OidcOnly => _oidcOnly && Enabled && !BreakGlassSet;

    /// <summary>
    /// Set by the operator on the host or in the container to re-enable local login. Read per call
    /// rather than cached so a restart with the variable set is all it takes.
    /// </summary>
    public static bool BreakGlassSet =>
        Environment.GetEnvironmentVariable(BreakGlassVariable) is "1" or "true" or "True";

    /// <summary>
    /// Whether the provider is the authority on permissions. When neither claim mapping is
    /// configured, a user's permissions are whatever Maki says they are and a sign-in never touches
    /// them; when either is, they are recomputed on every sign-in and local edits do not survive.
    /// </summary>
    public bool MapsPermissions => AdminClaim is not null || PermissionClaim.Length > 0;

    /// <summary>
    /// Whether the issuer is reached over TLS. Drives <c>RequireHttpsMetadata</c>, and is worth a
    /// startup warning when false: the id_token is signed, so a plain-HTTP provider is not forgeable,
    /// but the discovery document and JWKS are fetched in the clear and whoever can rewrite those
    /// chooses the signing key.
    /// </summary>
    public bool AuthorityIsHttps =>
        Authority.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>The keys this reads. Named once so the two loaders cannot drift apart.</summary>
    public static readonly string[] Keys =
    [
        SettingKeys.AuthOidcEnabled, SettingKeys.AuthOidcAuthority, SettingKeys.AuthOidcClientId,
        SettingKeys.AuthOidcClientSecret, SettingKeys.AuthOidcScopes, SettingKeys.AuthOidcDisplayName,
        SettingKeys.AuthOidcOnly, SettingKeys.AuthOidcAutoProvision, SettingKeys.AuthOidcUsernameClaim,
        SettingKeys.AuthOidcAdminClaim, SettingKeys.AuthOidcPermissionClaim
    ];

    /// <summary>
    /// Reads the settings straight out of the SQLite file, before the host is built.
    /// <para>
    /// It has to be this early, and so it cannot go through EF: whether the OpenID Connect scheme is
    /// registered at all is a service-registration decision, and registering it unconfigured breaks
    /// every request in the app. <c>AuthenticationMiddleware</c> walks every scheme on every request
    /// looking for one that wants to handle the callback, which materializes the handler's options,
    /// which fails validation with "The value cannot be an empty string. (Parameter 'ClientId')".
    /// </para>
    /// <para>
    /// Runs before migrations, which is safe because <c>AppConfig</c> predates every schema this can
    /// meet and nothing migrates these keys. A missing file or table means "not configured", which is
    /// exactly right for a fresh install.
    /// </para>
    /// </summary>
    public void Load(string databasePath)
    {
        var rows = new Dictionary<string, string>();

        try
        {
            if (File.Exists(databasePath))
            {
                using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT Key, Value FROM AppConfig WHERE Key LIKE 'auth.oidc%'";

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows[reader.GetString(0)] = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                }
            }
        }
        catch (SqliteException)
        {
            // A database from before AppConfig existed, or one still being created. Not configured.
        }

        Apply(rows);
    }

    /// <summary>The same load through EF, for tests and for anything holding a context already.</summary>
    public async Task LoadAsync(MakiDbContext db, CancellationToken ct = default)
    {
        var rows = await db.AppConfig
            .AsNoTracking()
            .Where(c => Keys.Contains(c.Key))
            .ToDictionaryAsync(c => c.Key, c => c.Value, ct);

        Apply(rows);
    }

    private void Apply(Dictionary<string, string> rows)
    {
        _enabled = rows.GetValueOrDefault(SettingKeys.AuthOidcEnabled) == "true";
        _oidcOnly = rows.GetValueOrDefault(SettingKeys.AuthOidcOnly) == "true";

        // Trailing slashes are stripped: the handler builds the discovery URL by concatenation, and
        // "https://idp/.well-known/..." with a doubled slash is a 404 on several providers.
        Authority = (rows.GetValueOrDefault(SettingKeys.AuthOidcAuthority) ?? string.Empty).Trim().TrimEnd('/');
        ClientId = (rows.GetValueOrDefault(SettingKeys.AuthOidcClientId) ?? string.Empty).Trim();
        ClientSecret = rows.GetValueOrDefault(SettingKeys.AuthOidcClientSecret) ?? string.Empty;

        Scopes = ParseScopes(rows.GetValueOrDefault(SettingKeys.AuthOidcScopes));

        DisplayName = Blank(rows.GetValueOrDefault(SettingKeys.AuthOidcDisplayName)) ?? DefaultDisplayName;
        UsernameClaim = Blank(rows.GetValueOrDefault(SettingKeys.AuthOidcUsernameClaim)) ?? DefaultUsernameClaim;

        AutoProvision = rows.GetValueOrDefault(SettingKeys.AuthOidcAutoProvision) == "true";
        AdminClaim = ClaimRule.Parse(rows.GetValueOrDefault(SettingKeys.AuthOidcAdminClaim));
        PermissionClaim = (rows.GetValueOrDefault(SettingKeys.AuthOidcPermissionClaim) ?? string.Empty).Trim();
    }

    /// <summary>
    /// Accepts either separator. The spec says space-delimited, every settings UI in the world gets
    /// typed with commas, and <c>openid</c> is dropped because the handler adds it unconditionally —
    /// listing it twice makes some providers reject the request outright.
    /// </summary>
    public static IReadOnlyList<string> ParseScopes(string? raw) =>
        (Blank(raw) ?? DefaultScopes)
        .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(s => !string.Equals(s, "openid", StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// A claim the provider must present, written <c>name=value</c> — or just <c>name</c>, in which case
/// any value satisfies it. Splitting on the first <c>=</c> only, because claim values routinely
/// contain one (a group distinguished name, a URL).
/// </summary>
public sealed record ClaimRule(string Name, string? Value)
{
    public static ClaimRule? Parse(string? raw)
    {
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var split = text.IndexOf('=');
        return split < 0
            ? new ClaimRule(text, null)
            : new ClaimRule(text[..split].Trim(), text[(split + 1)..].Trim());
    }

    public bool IsSatisfiedBy(IEnumerable<System.Security.Claims.Claim> claims) =>
        claims.Any(c =>
            string.Equals(c.Type, Name, StringComparison.OrdinalIgnoreCase) &&
            (Value is null || string.Equals(c.Value, Value, StringComparison.Ordinal)));
}
