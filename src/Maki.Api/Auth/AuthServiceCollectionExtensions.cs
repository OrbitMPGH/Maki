using System.Threading.RateLimiting;
using Maki.Api.Configuration;
using Maki.Core.Security;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Maki.Api.Auth;

public static class RateLimitPolicies
{
    /// <summary>
    /// Applied to sign-in, two-factor and first-run setup. Per client address, so one attacker
    /// cannot lock every account out by exhausting a shared budget.
    /// </summary>
    public const string Auth = "auth";
}

public static class AuthServiceCollectionExtensions
{
    /// <summary>
    /// Registers the whole authentication and authorization stack: Identity with an EF store, the
    /// session cookie, the per-user API key scheme, permission policies, CSRF, data protection and
    /// login rate limiting.
    /// </summary>
    /// <param name="oidc">
    /// Already loaded, because whether the OpenID Connect scheme is registered at all has to be
    /// decided here — see <see cref="OidcRuntimeOptions.Load"/> for why an unconfigured one cannot
    /// simply be registered and ignored.
    /// </param>
    public static IServiceCollection AddMakiAuth(
        this IServiceCollection services, AppPaths paths, OidcRuntimeOptions oidc)
    {
        services.AddSingleton<AuthRuntimeOptions>();
        services.AddSingleton(oidc);

        // Persist the data protection key ring next to the database.
        //
        // This is the single easiest thing to get wrong in this whole feature. Left unconfigured,
        // ASP.NET stores the key ring under the *user profile* — %LOCALAPPDATA% on Windows, and in a
        // container a directory that does not survive `docker compose up` recreating it. Every
        // restart would then issue a fresh key, silently invalidating every session cookie and every
        // antiforgery token: users are logged out for no visible reason and nothing appears in any
        // log. Keeping the keys in ConfigDir makes sessions survive restarts and upgrades.
        //
        // Consequences worth knowing: this directory is credential material (whoever holds it can
        // mint a session cookie for any user), it lives under the same filesystem-permissions
        // boundary as maki.db, and BackupService deliberately excludes it — a leaked backup must not
        // also hand over the ability to forge sessions.
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(paths.DataProtectionKeysDir))
            // Pinned so the purpose string does not change with the assembly name or content root,
            // either of which would orphan the existing key ring.
            .SetApplicationName("Maki");

        services.AddIdentityCore<MakiUser>(o =>
            {
                // NIST SP 800-63B: length is what matters, composition rules mostly produce
                // "Password1!" and a sticky note. A 10-character floor with no character-class
                // requirements is both stronger in practice and less likely to be written down.
                o.Password.RequiredLength = 10;
                o.Password.RequireDigit = false;
                o.Password.RequireLowercase = false;
                o.Password.RequireUppercase = false;
                o.Password.RequireNonAlphanumeric = false;
                o.Password.RequiredUniqueChars = 1;

                // Email is optional — this is a self-hosted app with no mail server, so there is
                // nothing to confirm an address against.
                o.User.RequireUniqueEmail = false;
                o.SignIn.RequireConfirmedEmail = false;
                o.SignIn.RequireConfirmedAccount = false;

                o.Lockout.AllowedForNewUsers = true;
                o.Tokens.AuthenticatorTokenProvider = TokenOptions.DefaultAuthenticatorProvider;
            })
            .AddEntityFrameworkStores<MakiDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();

        // Lockout thresholds come from settings, so they are applied in a second pass that can see
        // the startup-loaded values.
        services.AddOptions<IdentityOptions>().Configure<AuthRuntimeOptions>((o, auth) =>
        {
            o.Lockout.MaxFailedAccessAttempts = auth.LockoutMaxAttempts;
            o.Lockout.DefaultLockoutTimeSpan = auth.LockoutDuration;
            // Zero attempts means "never lock out" — expressed by switching lockout off rather than
            // by a zero threshold, which Identity would read as "lock on the first failure".
            o.Lockout.AllowedForNewUsers = auth.LockoutMaxAttempts > 0;
        });

        // OWASP's current PBKDF2-SHA256 guidance. Identity's .NET default is 100,000; the cost is
        // paid once per sign-in, not per request, so the higher figure is close to free here.
        services.Configure<PasswordHasherOptions>(o => o.IterationCount = 210_000);

        // AddIdentityCore does not register these (AddIdentity, which needs a role store, does), and
        // without them the cookie's OnValidatePrincipal has no validator to call — a disabled user's
        // session would then live until the cookie expired.
        services.TryAddScoped<ISecurityStampValidator, SecurityStampValidator<MakiUser>>();
        services.TryAddScoped<ITwoFactorSecurityStampValidator, TwoFactorSecurityStampValidator<MakiUser>>();
        services.Configure<SecurityStampValidatorOptions>(o =>
            // Default is 30 minutes. Disabling an account or revoking every session has to mean
            // something sooner than that; CurrentUserMiddleware closes the remaining gap per request.
            o.ValidationInterval = TimeSpan.FromMinutes(1));

        services.AddAuthentication(AuthSchemes.Adaptive)
            .AddPolicyScheme(AuthSchemes.Adaptive, AuthSchemes.Adaptive, o =>
                o.ForwardDefaultSelector = ctx =>
                    ctx.Request.Headers.ContainsKey(ApiKeyAuthenticationHandler.HeaderName)
                        ? AuthSchemes.ApiKey
                        : IdentityConstants.ApplicationScheme)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(AuthSchemes.ApiKey, null)
            .AddCookie(IdentityConstants.ApplicationScheme)
            .AddCookie(IdentityConstants.TwoFactorUserIdScheme, o =>
            {
                // Short-lived: it only has to survive the user reading a code off their phone.
                o.Cookie.Name = "Maki.TwoFactorUserId";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.ExpireTimeSpan = TimeSpan.FromMinutes(10);
            })
            .AddCookie(IdentityConstants.TwoFactorRememberMeScheme, o =>
            {
                o.Cookie.Name = "Maki.TwoFactorRememberMe";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.ExpireTimeSpan = TimeSpan.FromDays(30);
            })
            // Where the OpenID Connect handler deposits its result. It is not a session: the
            // callback endpoint reads it, decides which Maki account the subject belongs to, issues
            // the real session cookie and deletes this one. Keeping the two separate is what stops a
            // provider's ticket from being an authenticated Maki principal on its own.
            .AddCookie(IdentityConstants.ExternalScheme, o =>
            {
                o.Cookie.Name = "Maki.External";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.ExpireTimeSpan = TimeSpan.FromMinutes(10);
            });

        // Registered only when it is actually configured, and never conditionally *used*.
        //
        // AuthenticationMiddleware asks every registered scheme on every request whether it wants to
        // handle this one — that is how a remote handler intercepts its own callback path — and
        // asking materializes the handler's options. An OpenID Connect scheme with no client id
        // fails its own Validate() at that point, so registering it unconfigured does not lie
        // dormant: it throws on every request in the application, including the login page.
        if (oidc.Enabled)
        {
            services.AddAuthentication().AddOpenIdConnect(AuthSchemes.Oidc, o =>
            {
                o.Authority = oidc.Authority;
                o.ClientId = oidc.ClientId;
                o.ClientSecret = oidc.ClientSecret.Length > 0 ? oidc.ClientSecret : null;

                // Follows the issuer the operator actually typed. The handler otherwise refuses any
                // http:// authority outright — and refuses it by throwing while its options are
                // built, which AuthenticationMiddleware does on *every* request, so a single
                // http:// issuer would take the whole application down rather than only sign-in. A
                // provider on the same Docker network or LAN over plain HTTP is a normal
                // self-hosted arrangement; Program.cs logs a warning when this is what happens.
                o.RequireHttpsMetadata = oidc.AuthorityIsHttps;

                // Authorization code + PKCE. Never the implicit or hybrid flows: they put tokens in
                // the URL fragment, which is a place Maki has spent this whole feature getting
                // credentials out of.
                o.ResponseType = OpenIdConnectResponseType.Code;
                o.UsePkce = true;

                // The default is form_post, which arrives back as a cross-site POST — and a
                // cross-site POST only carries the correlation and nonce cookies if they are marked
                // SameSite=None, which browsers then refuse to store without Secure. The common Maki
                // deployment is plain HTTP on a LAN, where that combination means every sign-in
                // fails with "correlation failed" and nothing in the log says why. A query response
                // is a top-level GET, so Lax cookies come back and it works on http:// and https://
                // alike.
                o.ResponseMode = OpenIdConnectResponseMode.Query;

                // Explicit because it is the redirect URI the operator has to register with their
                // provider, and the settings card shows it. Handled inside UseAuthentication, so it
                // never reaches routing and needs no endpoint of its own.
                o.CallbackPath = OidcRuntimeOptions.CallbackPath;

                o.CorrelationCookie.SameSite = SameSiteMode.Lax;
                o.NonceCookie.SameSite = SameSiteMode.Lax;

                // Nothing here calls the provider's API on the user's behalf, so keeping the access
                // and refresh tokens would be storing credentials for no purpose.
                o.SaveTokens = false;
                o.GetClaimsFromUserInfoEndpoint = true;
                o.MapInboundClaims = false;

                o.Scope.Clear();
                o.Scope.Add("openid");
                foreach (var scope in oidc.Scopes)
                {
                    o.Scope.Add(scope);
                }

                // The ticket lands in the external cookie for AuthController to inspect, rather than
                // becoming a Maki session directly.
                o.SignInScheme = IdentityConstants.ExternalScheme;

                o.Events.OnRemoteFailure = ctx =>
                {
                    // Otherwise the handler rethrows and the user sees the developer exception page
                    // or a bare 500 — on a URL they arrived at from another site, with no way back.
                    ctx.HandleResponse();
                    ctx.Response.Redirect(
                        "/login?ssoError=" + Uri.EscapeDataString(ctx.Failure?.Message ?? "Sign-in failed"));
                    return Task.CompletedTask;
                };
            });
        }

        services.AddOptions<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme)
            .Configure<AuthRuntimeOptions>((o, auth) =>
            {
                o.Cookie.Name = "Maki.Session";
                o.Cookie.HttpOnly = true;
                o.Cookie.Path = "/";

                // Lax and not Strict on purpose: Strict would break the OIDC sign-in redirect, where
                // the browser arrives back at Maki from the identity provider's origin. Lax still
                // withholds the cookie from cross-site POST/PUT/DELETE, which is the CSRF-relevant
                // half, and AntiforgeryCookieFilter covers the rest.
                o.Cookie.SameSite = SameSiteMode.Lax;

                o.Cookie.SecurePolicy = auth.RequireHttps
                    ? CookieSecurePolicy.Always
                    : CookieSecurePolicy.SameAsRequest;

                o.SlidingExpiration = true;
                o.ExpireTimeSpan = auth.SessionLifetime;

                // The client is a SPA, so an unauthenticated API call must answer 401 and let the
                // app route to its own login screen. A 302 to a login *page* would be parsed as JSON
                // by every fetch in the frontend and fail with a syntax error instead.
                o.Events.OnRedirectToLogin = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                o.Events.OnRedirectToAccessDenied = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
                o.Events.OnValidatePrincipal = SecurityStampValidator.ValidatePrincipalAsync;
            });

        services.AddAuthorization(o => o.AddMakiPolicies());
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

        services.AddScoped<AdminGuard>();
        // Narrowed by CurrentUserMiddleware for a request; left unrestricted everywhere else, which is
        // every background job. See DataScope for why that default rather than deny-all.
        services.AddScoped<DataScope>();

        services.AddScoped<OidcSignInService>();

        services.AddScoped<CurrentUserContext>();
        services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<CurrentUserContext>());
        services.AddScoped<AuthEventLogger>();

        services.AddAntiforgery(o =>
        {
            o.HeaderName = "X-XSRF-TOKEN";
            // The secret half of the double-submit pair. Stays HttpOnly; the readable XSRF-TOKEN
            // cookie the SPA echoes is issued separately by AntiforgeryTokenMiddleware.
            o.Cookie.Name = "Maki.Antiforgery";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
        });

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(RateLimitPolicies.Auth, ctx => RateLimitPartition.GetFixedWindowLimiter(
                // Partitioned by address so a flood against one account cannot deny sign-in to
                // everyone else. Only meaningful behind a proxy once auth.trustedproxies is set —
                // until then every request looks like it comes from the proxy itself.
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    // No queue: a rejected sign-in attempt should fail immediately, not wait for a
                    // slot and give the caller a free retry.
                    QueueLimit = 0
                }));
        });

        return services;
    }
}
