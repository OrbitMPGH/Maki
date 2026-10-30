---
title: Reading in other apps
description: OPDS, KOReader sync and the Komga-compatible API.
---

## OPDS server

OPDS 1.2 with page streaming (OPDS-PSE), so Panels, Chunky, KOReader and Mihon read straight from
Maki. Off by default. Each user's feed URL carries its own rotatable token, and pages streamed this
way count as read.

## KOReader progress sync

Maki is a KOReader sync server. Chapters downloaded to KOReader report the page you reached, even
when read offline, and KOReader picks up where you stopped in Maki. It signs in with the OPDS token.
See [KOReader](/Maki/guides/reading/koreader/).

## Komga-compatible API

Apps that only connect to a Komga server, like the Mihon Komga extension and Tachimanga, can use
Maki instead. Off by default, switched on in the OPDS settings.

- Point the app at `http://your-host:8990/komga`.
- Use your OPDS token as the API key (or as the password, with any username).
- Reading progress syncs both ways, including through Mihon's Komga tracker.
- A client that cannot decode a page's format (an AVIF or WebP page) can ask for it as JPEG or PNG,
  as it would from Komga.
