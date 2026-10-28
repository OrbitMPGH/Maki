---
title: Reverse proxy and HTTPS
description: Put Maki behind HTTPS, set trusted proxies and turn on two-factor.
---

Before putting Maki on a public address:

1. **Terminate TLS in front of it**, then enable **Settings → System → Network → Require HTTPS**. This
   marks the cookie `Secure` and turns on HSTS. Do not enable it before TLS works: a `Secure`
   cookie over plain HTTP is never returned, so sign-in silently fails.
2. **List your reverse proxy under Trusted proxies** (IP or CIDR, for example `172.18.0.0/16`).
   Until you do, Maki ignores `X-Forwarded-For` and attributes every failed sign-in to the proxy.
   Maki does not trust the header from other addresses because a client could forge the audit log
   and get around rate limiting and lockout.
3. **Turn on two-factor authentication** under **Settings → Account → Sign-in**.
4. **Give each reader their own account.** A new account starts with OPDS and tracker access, no
   root folders, and no admin. If you run an identity provider, use
   [single sign-on](/Maki/installation/single-sign-on/) instead of handing out passwords.

Security settings are read at startup, so **restart Maki after changing them**.

Two directories under `/config` are credential material and deserve the same permissions as the
database: `dataprotection-keys` (whoever holds it can mint a session cookie for any user) and
`backups`. Backups exclude the key ring, which is why restoring onto a different machine signs
everyone out once.

API keys and OPDS feed URLs are shown **exactly once**. Only a SHA-256 fingerprint is stored, so a
lost key cannot be recovered; generate a new one.
