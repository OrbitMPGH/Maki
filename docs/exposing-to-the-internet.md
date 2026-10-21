# Exposing Maki to the internet

Maki authenticates with an HttpOnly session cookie and per-user API keys. Before putting it on a
public address:

1. **Terminate TLS in front of it**, then enable **Settings → System → Network → Require HTTPS**. This
   marks the cookie `Secure` and turns on HSTS. Do not enable it before TLS works: a `Secure`
   cookie over plain HTTP is never returned, so sign-in silently fails.
2. **List your reverse proxy under Trusted proxies** (IP or CIDR, for example `172.18.0.0/16`).
   Until you do, Maki ignores `X-Forwarded-For` and attributes every failed sign-in to the proxy.
   Trusting the header from any address would let a client forge the audit log and get around
   rate limiting and lockout.
3. **Turn on two-factor authentication** under **Settings → Account → Sign-in**.
4. **Give each reader their own account.** A new account starts with OPDS and tracker access, no
   root folders, and no admin. If you run an identity provider, use
   [single sign-on](single-sign-on.md) instead of handing out passwords.

Security settings are read at startup, so **restart Maki after changing them**.

Two directories under `/config` are credential material and deserve the same permissions as the
database: `dataprotection-keys` (whoever holds it can mint a session cookie for any user) and
`backups`. Backups exclude the key ring, which is why restoring onto a different machine signs
everyone out once.

API keys and OPDS feed URLs are shown **exactly once**. Only a SHA-256 fingerprint is stored, so a
lost key cannot be recovered; generate a new one.
