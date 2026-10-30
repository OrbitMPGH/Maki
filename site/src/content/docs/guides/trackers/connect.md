---
title: Connect a tracker
description: Link AniList, MyAnimeList, Kitsu, MangaBaka or MangaUpdates, and decide what Maki sends and imports.
---

Accounts are connected on the **Scrobble** page (`/scrobble`). Each tracker has its own block in
**Settings → Integrations → Trackers**.

- Trackers that use a redirect (AniList, MyAnimeList) have a **Connect** button on the Scrobble page.
  An admin must first enter the app's client ID and secret in Settings.
- The others have no Connect button. You save their credentials in Settings instead.
- **Sync now** on the Scrobble page stays disabled until one tracker is connected.

## AniList

1. An admin creates an API client at `anilist.co/settings/developer`. The redirect URL is your Maki
   address followed by `/api/v1/scrobble/oauth/anilist`.
2. The admin enters the client ID and client secret in **Settings → Integrations → Trackers**.
3. Press **Connect** on the Scrobble page and approve the login on AniList.

AniList tokens last about a year and cannot be refreshed. When the token expires, connect again.

AniList and MyAnimeList both have an anime list. The **Use anime for recommendation taste** switch
lets Discover learn from it, once **Anime you've watched** is also on in the Taste tab. See
[Discover](/Maki/features/discover/).

## MyAnimeList

Enter the client ID and secret in the same Trackers card, then press **Connect**. Maki refreshes the
token on its own, so you do not need to reconnect.

## Kitsu

Enter your Kitsu **Email** and **Password** in the Trackers card. Maki signs in the first time it
needs Kitsu.

## MangaBaka

Copy a personal access token from your MangaBaka settings and paste it into **Personal Access
Token**.

## MangaUpdates

Enter your MangaUpdates **Username** and **Password**. Maki signs in on first use.

MangaUpdates limits how often writes can be sent, so Maki batches them and an update can take a
minute or so to show on mangaupdates.com. Entries on custom MangaUpdates lists are not imported.

## What Maki syncs

Every tracker block has these switches:

- **Scrobble reading** sends your progress. See
  [What counts as read](/Maki/guides/trackers/what-counts-as-read/) for when a chapter is sent.
- **Sync ratings** sends your final Maki ratings. See
  [Ratings and "so far" scores](/Maki/guides/trackers/ratings/) for how they are set.
- **Also sync "so far" ratings** sends the scores you set partway through a series. It needs
  **Sync ratings** on. The [ratings guide](/Maki/guides/trackers/ratings/#sending-ratings-to-trackers)
  covers the one-time question Maki asks about it.
- On AniList and MyAnimeList, **Use anime for recommendation taste** controls whether Discover reads
  your anime list.

Series in incognito, at either level, are never sent to a tracker.

Progress only moves forward. Maki never lowers a tracker's progress and never demotes a completed
entry.

Each block also has an **Import ratings** button, enabled once the tracker is connected, which brings
ratings from the tracker into Maki. Imported scores always become final ratings.

The Scrobble page runs a sync on a schedule (an admin sets the interval) and shows the last and next
sync. Series Maki cannot match with confidence appear under **Needs review**, where you can
**Assign** them to a MangaBaka entry or **Ignore** them.

## Importing your lists

Import lists add the series on your tracker lists to your library. Connect the tracker first, then
open **Settings → Integrations → Import lists** and turn on its switch.

- **Import entries with status.** Any of **Reading**, **Plan to read** and **Completed**. At least
  one must stay selected.
- **Root folder.** Where new series are filed. Defaults to the first folder you can see.
- **Monitor for new chapters** and **New chapters.** The latter is **All**, **Smart** (only the next
  few ahead of your reading) or **Main only** (skips specials).
- **Max per run** caps how many entries one run handles.

**Run now** starts a sync. **Sync everything** pulls the whole list again and asks for confirmation.

If your account cannot add series directly, Maki files each entry as a request for an admin to
approve. Entries Maki could not match, or that you ignored, are listed under **Not matched or
ignored**, where you can retry or un-ignore them.

To send unread series the other way, turn on **Add unread series as plan-to-read** in the Trackers
card. Series with no progress are added to your tracker list as Plan to read. Entries already on your
lists are never changed.
