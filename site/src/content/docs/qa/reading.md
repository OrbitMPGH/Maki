---
title: Reading apps and sync
description: Which apps work with Maki, offline reading, iOS storage limits, AVIF pages and KOReader matching.
---

### Which apps can I read with?

You can read in Maki's own reader, or over OPDS in Panels, Chunky, KOReader and Mihon. Mihon's Komga
extension and Tachimanga connect through Maki's Komga-compatible API instead. Any comic reader that
opens CBZ files can also read the same folder. See
[Reading over OPDS and Komga](/Maki/guides/reading/opds-apps/).

### Why are offline reading and push notifications missing?

Both need HTTPS, or Maki opened on `localhost`. Browsers only allow service workers, offline storage
and push on a secure connection, so a plain `http://` address on your LAN hides them. Put Maki behind
a reverse proxy with a certificate, as in [Reverse proxy and HTTPS](/Maki/installation/reverse-proxy-https/).
Add to Home Screen still works over plain HTTP, without the offline parts.

### Why did my saved chapters disappear on my iPhone or iPad?

Safari clears a site's data after seven days without use, unless Maki is added to the home screen.
Push also only reaches the installed app on iOS. Add Maki to the home screen before you save chapters.
See [Install as an app and read offline](/Maki/guides/reading/offline-and-app/).

### An AVIF page will not open in KOReader or on iOS. What do I do?

AVIF does not open in KOReader or on iOS before 16. Under **Settings → Library → Page conversion**,
turn conversion on and pick WebP. New downloads are stored in that format, and a library pass converts
the files already on disk. WebP opens in every reader Maki works with.

### KOReader does not sync my progress or does not recognise a book. Why?

Set **Document matching method** to **Binary**. Binary matching needs the copy on the device to be
byte-for-byte a file from your library, and Maki records those ids when it serves a download and
during a library pass every six hours. Filename matching is unreliable across series, so avoid it.
The full setup is in [KOReader](/Maki/guides/reading/koreader/).

### Does reading in another app count in Maki?

Yes. Pages streamed over OPDS count as read, and KOReader progress goes through the same path as
Maki's reader. Reading progress also reaches AniList, MyAnimeList, Kitsu, MangaBaka and MangaUpdates,
whether you read in Maki, over OPDS or in Kavita.
