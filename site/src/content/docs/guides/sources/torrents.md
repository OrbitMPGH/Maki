---
title: Torrents with Prowlarr and Jackett
description: Search torrent indexers through Prowlarr or Jackett, send releases to qBittorrent, Transmission or Deluge, and import the finished files as CBZs.
---

Torrents are an optional second path next to the scraper sources. Maki searches an indexer for a
release, sends the torrent to a download client, and imports the files when the download finishes.
Using torrents needs both an indexer and a download client. Scraper sources need neither.

## Indexers

Set the indexer under **Settings → Downloads → Indexer**.

1. Pick the **Indexer**: **Prowlarr** or **Jackett**.
2. Enter its **URL**. The placeholders are `http://localhost:9696` for Prowlarr and
   `http://localhost:9117` for Jackett.
3. Enter the **API key** from that indexer, then select **Test**.
4. Choose which indexers to search and which categories to use, then save.

Maki uses one indexer at a time. Switching from Prowlarr to Jackett, or back, makes release
suggestions you dismissed earlier stop matching, because the two number releases differently.

## Download clients

Set the client under **Settings → Downloads → Download client**. Maki supports three clients:
qBittorrent, Transmission and Deluge. Only one is active at a time.

- **URL**: the client's web address. Each client shows its own default as a placeholder.
- **Username**: used by qBittorrent and Transmission. Deluge's web UI has only a password, so the
  field is hidden for it.
- **Password**
- **Label**: the tag Maki puts on its torrents so it can find them again. The placeholder is `maki`.
  Maki refuses a label that Deluge won't accept.
- **Path as the download client reports it** and **Path as Maki sees it**: fill these in when the
  client and Maki see the downloads folder at different paths, for example when they run in different
  containers. Without them, Maki can't find the files after a download.

Three switches under **Finished torrents** control what happens after an import:

- **Hardlink imported torrents when possible** keeps the torrent's files in place and links them into
  your library, so the torrent can keep seeding. Hardlinks need both folders on the same filesystem.
- **Remove torrents from the download client after import**
- **Also delete the downloaded files** removes the files along with the torrent.

## Getting a release

There are two ways to grab a torrent by hand, and one way to have Maki look for them.

- On a series page, select **Search releases**. Pick a release in the search results and select
  **Grab**.
- In the series actions menu, select **Search volume releases** to look for a whole-volume release.
- Under **Settings → Downloads → Volume releases**, turn on **Search torrents for volume releases** to
  have Maki search for them automatically. Set **Auto-grab size limit (MB)** to decide which releases
  Maki grabs on its own. Larger releases become proposals you approve by hand.

Volume releases need an indexer and a download client, as the card itself says.

## From torrent to CBZ

1. The grab sends the torrent to your download client, tagged with the label you set. The download
   appears in the Activity queue.
2. Maki checks the client for progress. When the torrent completes, Maki reads where the client saved
   it, through your path settings.
3. Maki imports the files into the series folder. CBZ and ZIP files are placed unchanged, since a CBZ is a ZIP.
   `.cbr`, `.rar`, `.7z` and `.tar` files are repacked as CBZ. A folder of loose images becomes one CBZ
   per folder. A PDF is kept as it is and read in place.
4. If an import would replace chapter files you already have, the queue asks you to decide before
   anything changes.

The [monitoring and downloads](/Maki/features/monitoring-and-downloads/) page covers the rest of the
import rules, including how Maki rejects a release that names a related work better than the series.

## When a torrent stalls

The queue reports these problems:

- **The torrent never appeared in the download client**: Maki added it, but the client doesn't list it.
  Check the client's URL, login and label.
- **The torrent is no longer in the download client**: someone removed it from the client.
- **The download client reports an error on this torrent**: open the torrent in the client for its
  own message.
