---
title: Komga-compatible apps
description: Connect apps that only speak Komga's API, such as the Mihon Komga extension and Tachimanga, to Maki.
---

Maki serves a Komga-compatible API for apps that can only connect to a Komga server. Apps that read
OPDS feeds, such as Panels, Chunky, KOReader and Mihon's OPDS source, use the feed instead. See
[Reading over OPDS](/Maki/guides/reading/opds-apps/).

## Turn it on

In **Settings → Reading → OPDS catalogue**, turn on **Enable the OPDS catalogue** and confirm your
password, then turn on **Komga-compatible API**. Both must stay on for the API to answer.

The card shows the two values an app needs:

- **Server address**, for example `http://your-host:8990/komga`.
- **API key**, which is your OPDS token. It is the last part of the feed URL, after `/opds/`.

## Connect the app

Enter the server address and the API key. If the app asks for a username and password instead, use
any username and the API key as the password.

- Regenerating the OPDS token signs these apps out, so they need the new key.
- Turning off either switch stops them from connecting.
- API keys with full access do not open this API. Use the OPDS token.

## Progress

Apps report progress to Maki, and Maki reports read state back. Mihon's Komga tracker and Komga
clients that track reading do this. The progress goes through the same path as the built-in reader,
so it counts in Stats and reaches your trackers and Kavita. For the full picture, see
[How progress syncs between apps](/Maki/guides/reading/progress-sync/).

- A single chapter the app reports as read counts as a read.
- A jump over several chapters marks them read in one go, so it does not show up as a day's reading
  in Rewind.
- The API only marks chapters read. It never marks one unread.

Loading a page does not count as progress here, unlike OPDS.

## Formats an app cannot show

Apps that cannot decode WebP or AVIF can ask for JPEG or PNG instead, as with Komga. Maki converts
the page for that request, which uses server CPU.

:::tip
AVIF pages do not open in KOReader or on iOS. If you read on those, keep page conversion on a format
they can open. See [Files and library](/Maki/features/files-and-library/).
:::
