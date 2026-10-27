---
title: Accounts and security
description: Two-factor, exposing Maki to the internet, library access for new users, single sign-on and lost keys.
---

### How do I turn on two-factor authentication?

Go to **Settings → Account → Sign-in** and add two-factor authentication. Turn it on for every account
that can reach Maki from outside your network. See
[Reverse proxy and HTTPS](/Maki/installation/reverse-proxy-https/).

### Can I expose Maki to the internet?

Yes, once HTTPS works end to end. Put TLS in front of Maki first, then turn on **Require HTTPS** under
**Settings → System → Network**. List your reverse proxy under **Trusted proxies** too, or Maki
attributes every failed sign-in to the proxy and rate limiting sees the wrong client. Restart Maki
after changing either setting, since the network settings take effect at startup.

### Why does a new user see an empty library?

New accounts start with no root folder access, and an account with no grants sees an empty library.
Grant the root folders under **Settings → Users & security → Users**. The same grants decide where
someone with the **Add series** permission may add a title. See
[Users and library access](/Maki/guides/running/users-and-access/).

### Can I sign in with my identity provider?

Yes. Maki supports OpenID Connect single sign-on, which works alongside local passwords. Fill in
**Settings → Users & security → Single sign-on**, then restart Maki. The
[single sign-on guide](/Maki/installation/single-sign-on/) covers the redirect URI and claim mapping.

### I lost an API key or OPDS link. Can I get it back?

No. API keys and OPDS feed URLs are shown only once, and Maki stores just a fingerprint of them. Create
a new key under **Settings → Account → API keys**. Regenerating the OPDS token also signs KOReader out,
so log it in again with the new token.

### Why does sign-in give the same error for every failure?

Maki answers every failed sign-in with the same 401. That covers a wrong password, an unknown username,
a disabled account and a lockout, so the login page cannot be used to find out which accounts exist.
