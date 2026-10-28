---
title: Reading apps and sync
description: Which apps work with Maki, offline reading, iOS storage limits, AVIF pages and KOReader matching.
---

### Which apps can I read with?

Maki's own reader, or Panels, Chunky, KOReader and Mihon over OPDS. Mihon's Komga extension and Tachimanga use the Komga-compatible API instead. Any CBZ reader can also open the folder. See
[Reading over OPDS and Komga](/Maki/guides/reading/opds-apps/).

### Why are offline reading and push notifications missing?

Browsers allow service workers, offline storage and push only on HTTPS or `localhost`, so a plain `http://` LAN address hides them. Use a reverse proxy with a certificate, see [Reverse proxy and HTTPS](/Maki/installation/reverse-proxy-https/). Add to Home Screen still works over HTTP, without offline.

### Why did my saved chapters disappear on my iPhone or iPad?

Safari clears a site's data after seven days without use unless Maki is on the home screen, and iOS push only reaches the installed app. Add Maki to the home screen before saving chapters. See [Install as an app and read offline](/Maki/guides/reading/offline-and-app/).

### An AVIF page will not open in KOReader or on iOS. What do I do?

KOReader and iOS before 16 cannot open AVIF. Under **Settings → Library → Page conversion**, turn conversion on and pick WebP. New downloads use it, and a library pass converts existing files. Every reader Maki works with opens WebP.

### KOReader does not sync my progress or does not recognise a book. Why?

Set **Document matching method** to **Binary**. It needs the device copy to be byte-for-byte a file from your library. Maki records those ids when it serves a download and in a library pass every six hours. Filename matching is unreliable across series. Full setup: [KOReader](/Maki/guides/reading/koreader/).

### Does reading in another app count in Maki?

Yes. Pages streamed over OPDS count as read, and KOReader progress uses the same path as Maki's reader. Progress reaches AniList, MyAnimeList, Kitsu, MangaBaka and MangaUpdates whether you read in Maki, over OPDS or in Kavita.
