---
title: Accounts and security
description: Two-factor, exposing Maki to the internet, library access for new users, single sign-on and lost keys.
---

### How do I turn on two-factor authentication?

Add it under **Settings → Account → Sign-in**, for every account that can reach Maki from outside your network. See [Reverse proxy and HTTPS](/Maki/installation/reverse-proxy-https/).

### Can I expose Maki to the internet?

Yes, once HTTPS works end to end. Put TLS in front of Maki, then turn on **Require HTTPS** under **Settings → System → Network**. Also list your reverse proxy under **Trusted proxies**, or Maki blames the proxy for every failed sign-in and rate limiting sees the wrong client. Restart Maki afterwards, because network settings apply at startup.

### Why does a new user see an empty library?

New accounts have no root folder access. Grant folders under **Settings → Users & security → Users**. The same grants decide where someone with **Add series** may add a title. See [Users and library access](/Maki/guides/running/users-and-access/).

### Can I sign in with my identity provider?

Yes, with OpenID Connect alongside local passwords. Fill in **Settings → Users & security → Single sign-on** and restart Maki. The [single sign-on guide](/Maki/installation/single-sign-on/) covers the redirect URI and claim mapping.

### I lost an API key or OPDS link. Can I get it back?

No. Both are shown once and Maki stores only a fingerprint. Create a new key under **Settings → Account → API keys**. Regenerating the OPDS token also signs KOReader out, so log it in again with the new token.

### Why does sign-in give the same error for every failure?

A wrong password, unknown username, disabled account and lockout all return the same 401, so the login page cannot reveal which accounts exist.
