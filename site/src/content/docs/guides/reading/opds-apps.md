---
title: Reading over OPDS and Komga
description: Connect Panels, Chunky, Mihon and other apps to Maki.
---

Outside apps can read from Maki over OPDS or the Komga-compatible API. Both are off by default.

## OPDS

OPDS 1.2 with page streaming (OPDS-PSE) works with Panels, Chunky, KOReader and Mihon. Each user's
feed URL carries its own rotatable token. Each page an app streams saves your position. A chapter
completes on its last page after you open it, so an app that fetches that page early does not mark
it read.

Each chapter entry shows the chapter's first page as its thumbnail. If that page will not render,
the series cover is used instead. Archived series are left out of the feed. Restoring one brings it
back. See [Archiving a series](/Maki/guides/library/archiving/).

1. Turn on the OPDS catalogue under **Settings → Reading → OPDS catalogue**.
2. Copy the feed URL it shows. It is shown once, right after you turn the catalogue on or regenerate
   the token.
3. Paste it into your app as a new catalogue.

From outside your network, swap the host for the address you use there. See
[Reverse proxy and HTTPS](/Maki/installation/reverse-proxy-https/).

For KOReader, which can also sync progress, see [KOReader](/Maki/guides/reading/koreader/).

## Komga-compatible API

Apps that only connect to a Komga server, like the Mihon Komga extension and Tachimanga, can use
Maki's Komga-compatible API instead. See [Komga-compatible apps](/Maki/guides/reading/komga-apps/).
