---
title: Komga-compatible apps
description: Connect apps that only speak Komga's API, such as the Mihon Komga extension and Tachimanga, to Maki.
---

Maki serves a Komga-compatible API. Use it for apps that can only connect to a Komga server. Apps
that read OPDS feeds, such as Panels, Chunky, KOReader and Mihon's OPDS source, use the feed instead.
See [Reading over OPDS](/Maki/guides/reading/opds-apps/).

## Turn it on

Open **Settings → Reading → OPDS catalogue**, turn on **Enable the OPDS catalogue** and confirm your
password. Then turn on **Komga-compatible API**. The Komga switch only appears once the catalogue is on,
and both must stay on for the API to answer.

The card shows the two values an app needs:

- **Server address**, for example `http://your-host:8990/komga`.
- **API key**, which is your OPDS token. It is the last part of the feed URL, after `/opds/`.

## Connect the app

Enter the server address in the app, then the API key. Some apps ask for a username and a password
instead. In that case use any username, with the API key as the password.

Regenerating the OPDS token signs these apps out too, so they need the new key. Turning off either
switch stops them from connecting.

API keys with full access do not open this API. Use the OPDS token.

## Progress

The app reports progress to Maki, and Maki reports read state back to the app. Mihon's Komga tracker
does this, and so do Komga clients that track reading.

Progress from these apps goes through the same path as the built-in reader. It counts in Stats, and it
reaches your trackers and Kavita like any other read.

- A single chapter the app reports as read counts as a read.
- A jump over several chapters marks them read in one go, so it does not show up as a day's reading
  in Rewind.
- The API only marks chapters read. It never marks one unread.

Loading a page does not count as progress here, unlike OPDS.

## Formats an app cannot show

Some apps cannot decode WebP or AVIF pages. They can ask for JPEG or PNG instead, the same way Komga
does. Maki converts the page for that request, which uses server CPU.

:::tip
AVIF pages do not open in KOReader or on iOS. If you read on those, keep page conversion on a format
they can open. See [Files and library](/Maki/features/files-and-library/).
:::
