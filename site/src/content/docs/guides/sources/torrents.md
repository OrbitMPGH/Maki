---
title: Torrents with Prowlarr and Jackett
description: Search torrent indexers through Prowlarr or Jackett, send releases to qBittorrent, Transmission or Deluge, and import the finished files as CBZs.
---

Torrents are an optional second path next to the scraper sources. Maki searches an indexer for a
release, sends the torrent to a download client, and imports the files when it finishes. This needs
both an indexer and a download client. Scraper sources need neither.

## Indexers

Under **Settings → Downloads → Indexer**:

1. Pick **Prowlarr** or **Jackett**.
2. Enter its **URL** (placeholders: `http://localhost:9696` for Prowlarr, `http://localhost:9117` for
   Jackett).
3. Enter the indexer's **API key**, then select **Test**.
4. Choose which indexers to search and which categories to use, then save.

Maki uses one indexer at a time. Switching between Prowlarr and Jackett makes release suggestions you
dismissed earlier stop matching, because the two number releases differently.

## Download clients

Under **Settings → Downloads → Download client**, choose qBittorrent, Transmission or Deluge (one
active at a time).

- **URL**: the client's web address. Each client shows its own default as a placeholder.
- **Username**: used by qBittorrent and Transmission. Deluge's web UI has only a password, so the
  field is hidden.
- **Label**: the tag Maki puts on its torrents so it can find them again (placeholder `maki`). Maki
  refuses a label that Deluge won't accept.
- **Path as the download client reports it** and **Path as Maki sees it**: fill in both when the
  client and Maki see the downloads folder at different paths, for example in different containers.
  Without them, Maki can't find the files after a download.

The **Finished torrents** switches control what happens after an import:

- **Hardlink imported torrents when possible** links the files into your library and leaves them in
  place, so the torrent can keep seeding. Both folders must be on the same filesystem.
- **Remove torrents from the download client after import**
- **Also delete the downloaded files** removes the files along with the torrent.

## Getting a release

- On a series page, select **Search releases**, pick a result and select **Grab**.
- In the series actions menu, **Search volume releases** looks for a whole-volume release.
- To search automatically, turn on **Search torrents for volume releases** under **Settings →
  Downloads → Volume releases**. **Auto-grab size limit (MB)** sets which releases Maki grabs on its
  own. Larger ones become proposals you approve by hand.

## From torrent to CBZ

1. The grab sends the torrent to your download client, tagged with the label you set. The download
   appears in the Activity queue.
2. Maki checks the client for progress. When the torrent completes, Maki reads where the client saved
   it, through your path settings.
3. Maki imports the files into the series folder. CBZ and ZIP files are placed unchanged, since a
   CBZ is a ZIP. `.cbr`, `.rar`, `.7z` and `.tar` files are repacked as CBZ, and a folder of loose
   images becomes one CBZ per folder. A PDF is kept as it is and read in place.
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
