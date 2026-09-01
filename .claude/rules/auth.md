---
paths:
  - "src/Maki.Api/Controllers/Auth*.cs"
  - "src/Maki.Api/Controllers/Account*.cs"
  - "src/Maki.Api/Controllers/Users*.cs"
  - "src/Maki.Api/Controllers/Opds*.cs"
  - "src/Maki.Api/Controllers/Search*.cs"
  - "src/Maki.Api/Auth/**"
  - "src/Maki.Api/Program.cs"
  - "src/Maki.Api/Services/*Auth*.cs"
  - "src/Maki.Api/Services/*Oidc*.cs"
  - "src/Maki.Api/Services/Opds*.cs"
  - "src/Maki.Api/Hubs/**"
  - "src/Maki.Core/Security/**"
---

# Auth, OIDC, tokens

Migrated out of the root CLAUDE.md so this only loads when touching auth/OPDS/hub code.

- **No instance API key.** Auth is ASP.NET Identity: HttpOnly session cookie (`Maki.Session`) for the SPA, per-user `UserApiKey` rows for everything else. `?apikey=` query param is accepted **nowhere** (`ApiKeyAuthenticationHandler` reads only the `X-Api-Key` header — query strings land in browser history/Referer/proxy logs). Reader images/thumbnails and the SignalR handshake are same-origin and ride the cookie instead. Only `UserApiKey.KeyHash` (SHA-256) is stored, never the key — it's displayed exactly once, on mint.
- **`{ConfigDir}/dataprotection-keys` is credential material.** Signs session cookies + antiforgery tokens; whoever holds it can mint a session for any user. `BackupService` must never include it (leaked backup = permanent auth bypass). Unconfigured, ASP.NET puts the ring under the user profile — in Docker that doesn't survive container recreation, silently logging everyone out with nothing in the logs.
- **Authorization fails closed** via `AuthorizationOptions.FallbackPolicy` — a controller with no `[Authorize]` still requires sign-in. `MapFallbackToFile("index.html")` needs `.AllowAnonymous()` or the login page 401s; `OpdsController` needs class-level `[AllowAnonymous]` (authenticates its own path token). Permissions check against `ICurrentUser` (DB read/request, cached per-request), not cookie claims — revoking takes effect next request. Always test with `MakiPermissions.Grants`, never bare `HasFlag` (`Admin` only holds its own bit). `MakiPermission` values persist in `AspNetUsers.Permissions` — append only, never renumber/reuse.
- **Antiforgery tokens are bound to the identity issued to.** `AntiforgeryTokenMiddleware` reissues on **every** GET, not just when missing, or the anonymous token keeps serving and every post-login mutation fails. `AuthController.CompleteSignInAsync` also reissues directly since `SignInAsync` doesn't update `HttpContext.User` mid-request. Header-credential (API key) requests are exempt — nothing ambient to forge.
- **`SettingsController` is admin per action, not per class.** `GET ui/metadata/setup/reader/library` are exceptions (needed before knowing if user is admin). Everything else, and every write, is admin-only — reads return Prowlarr/Kavita/qBittorrent secrets in plaintext.
- **Last-admin rule (`AdminGuard`)**: can't drop your own `Admin` flag, disable/delete yourself, or strip the flag from the last admin — that state is unrecoverable except by hand-editing `maki.db`. "Usable" excludes `Disabled`/`PendingSetup` accounts.
- **OPDS token is a `UserApiKey` with `Scope = Opds`**, not the old instance-wide `opds.token`. An `Opds` key is rejected on `/api/v1/*` and vice versa — the feed URL gets pasted into third-party apps and shouldn't carry management-API power. `OpdsAccessService.ResolveAsync` returns null (**404, never 401**) for disabled catalogue/unknown token/disabled or unpermissioned owner.
- **Cover proxy (`GET search/cover`) is an SSRF primitive**, gated by `CoverHostPolicy` + `ISource.CoverHosts` (own domain + subdomains, or declared CDN domains — Webtoons `pstatic.net`, MangaPlus `tokyo-cdn.com`, Asura `asuracomic.net`). Match requires a dot boundary or `evil-mangadex.org` would pass a suffix test.
- **`auth.*` settings load once into `AuthRuntimeOptions` at startup — need a restart.** `auth.requirehttps` defaults **off** (common deploy is plain HTTP LAN; a `Secure` cookie set there never comes back). `auth.trustedproxies` defaults **empty** (honoring `X-Forwarded-For` from anyone lets a client forge the audit log / dodge lockout). `auth.lockoutmaxattempts = 0` means "no lockout" and must stay 0, not fall back to Identity's default.
- **OIDC scheme registers only when configured, read *before* the host builds** (`OidcRuntimeOptions.Load`, raw SQLite, tolerates missing config). Registering it unconfigured is not inert — every request asks every scheme if it wants to handle it, which materializes options and an empty `ClientId` 500s **the whole app including login**. `RequireHttpsMetadata` derives from whether the issuer is `https://` (LAN OIDC over plain HTTP is normal). `ResponseMode = query` not the handler's `form_post` default — a form-post callback is cross-site and needs `SameSite=None`+`Secure` cookies, which breaks on plain HTTP.
- **`MapInboundClaims` is off** — claims keep provider-sent names (`groups`, `email`) so `auth.oidcadminclaim`/`oidcpermissionclaim` (configured by name) actually match. Means `GetExternalLoginInfoAsync` doesn't work; `AuthController.OidcCallback` reads `sub` directly.
- **SSO resolves by subject, then by *verified* email, provisions only if told to.** Email match needs `email_verified` **and** exactly one local match (no unique-email constraint here). Name collisions refused, not merged. `auth.oidcautoprovision` defaults **off**; provisioned accounts get zero root folders/grants. Claim-to-permission mapping is all-or-nothing: unset claims means Maki's user list stays authoritative; set claims recompute permissions on every sign-in (a manual edit on Users page won't survive).
- **`MAKI_ALLOW_LOCAL_LOGIN=1` is the OIDC break-glass** — must stay an env var (a UI-reachable value would be no escape hatch if the provider is down). Admins are always exempt from `oidconly`.
- **`UseStaticFiles` runs before `UseAuthentication` — don't move it.** `/assets/index-*.js` matches no endpoint (`MapFallbackToFile` excludes anything with a file extension) so it'd fall under the fail-closed `FallbackPolicy` and 401 for signed-out users, leaving a blank unrecoverable login page.
- **Login answers one generic 401 for every failure** (unknown user/wrong password/disabled/lockout) to avoid account enumeration. Unknown usernames still burn a dummy PBKDF2 hash so timing doesn't leak which case it was.
- **`EventsHub` requires auth, addresses groups not `Clients.All`** — `user-{id}` per connection, admins also join `admins`. Group membership is fixed for the connection's life.
