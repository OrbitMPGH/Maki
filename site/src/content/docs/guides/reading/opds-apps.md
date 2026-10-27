---
title: Reading over OPDS and Komga
description: Connect Panels, Chunky, Mihon and other apps to Maki.
---

Maki has two ways for outside apps to read from it. Both are off by default and switched on in the
OPDS settings.

## OPDS

OPDS 1.2 with page streaming (OPDS-PSE) works with Panels, Chunky, KOReader and Mihon. Each user's
feed URL carries its own rotatable token, and pages streamed this way count as read.

1. Turn on the OPDS catalogue under **Settings → Reading → OPDS catalogue**.
2. Copy the feed URL it shows.
3. Paste it into your app as a new catalogue.

From outside your network, swap the host for the address you use there. See
[Reverse proxy and HTTPS](/Maki/installation/reverse-proxy-https/).

For KOReader, which can also sync progress, see [KOReader](/Maki/guides/reading/koreader/).

## Komga-compatible API

Apps that only connect to a Komga server, like the Mihon Komga extension and Tachimanga, can use
Maki instead.

- Server address: `http://your-host:8990/komga`
- API key: your OPDS token (or use it as the password with any username)

Reading progress syncs both ways, including through Mihon's Komga tracker. A client that cannot
decode a page's format (AVIF or WebP) can ask for it as JPEG or PNG, as it would from Komga.

:::tip
AVIF pages do not open in KOReader or on iOS. If you use those, keep page conversion on a format
they support.
:::
