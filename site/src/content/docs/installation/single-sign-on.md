---
title: Single sign-on (OpenID Connect)
description: OpenID Connect setup and claim mapping.
---

Single sign-on is optional and works alongside local passwords. Tested against Authelia, Keycloak,
Authentik, and Entra ID; anything with OpenID Connect discovery and the authorization code flow
should work.

Register Maki with your provider as a confidential or public client using the authorization code
flow with PKCE. The redirect URI is `https://<your-host>/api/v1/auth/oidc/callback`. Then fill in
**Settings → Single sign-on** and **restart Maki**; the provider configuration is read once at
startup.

| Field | Notes |
|---|---|
| Issuer URL | For example `https://auth.example.com`. Maki appends `/.well-known/openid-configuration`. |
| Client ID / secret | Leave the secret empty for a public client; PKCE protects the exchange either way. |
| Scopes | `openid` is always requested. Add `groups` (or your provider's equivalent) for claim mapping. |
| Create accounts on first sign-in | Off by default. When on, anyone the provider authenticates gets an account. Fine for a household realm; leave it off for a shared company realm. |
| Admin claim / Permission claim | Optional, see below. |

- **New accounts start with no library access.** Grant a root folder under **Settings → Users**.
- **Linking.** An account is identified by the provider's `sub` claim, so upstream renames do not
  strand anyone. The first time an unknown subject signs in, Maki links it to a local account with
  the same email if the provider marks the address as verified and exactly one account has it.
  Otherwise it creates an account (if allowed) or refuses.
- **Claim mapping is all-or-nothing.** With both claim fields empty, the provider only says *who*
  someone is and permissions stay as set on the Users page. Fill either one in and the provider
  becomes the authority: permissions are recomputed on every sign-in, and edits on the Users page
  are overwritten. The admin claim is `claim=value` (`groups=maki-admins`); the permission claim is
  a claim name whose values are matched against permission names (`DownloadChapters`, `UseOpds`,
  and so on). Unmatched values are ignored, and `Admin` only comes from the admin claim.
- **Require single sign-on** refuses password sign-in for everyone **except administrators**, so a
  provider outage never locks you out of your own library. If you are locked out anyway, set
  `MAKI_ALLOW_LOCAL_LOGIN=1` and restart: password sign-in returns for every account, and Maki logs
  a warning and shows a banner until you remove it.
- **`http://` issuers** are allowed for a provider on the same LAN or Docker network, with a
  startup warning. Tokens are signed either way, but the discovery document and signing keys travel
  in the clear. Prefer `https://` where you can.
