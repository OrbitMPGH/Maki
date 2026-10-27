---
title: Connect a tracker
description: Link AniList, MyAnimeList, Kitsu, MangaBaka or MangaUpdates, and decide what Maki sends and imports.
---

This page covers how to link each tracker to Maki, what Maki sends to it, and how to import your lists back. Accounts are connected on the Scrobble page. For AniList and MyAnimeList, an admin has to enter the app credentials in Settings first.

## Before you start

- Open **Settings → Integrations → Trackers** to see every tracker's block. Admins also enter the AniList and MyAnimeList client ID and secret there.
- Open the **Scrobble** page (`/scrobble`) and press **Connect** on the tracker you want. Trackers that do not use a redirect have no Connect button. Their credentials are saved in Settings instead.
- **Sync now** on the Scrobble page stays disabled until one tracker is connected.

## AniList

AniList uses an OAuth login.

1. An admin creates an API client at `anilist.co/settings/developer`. The redirect URL is your Maki address followed by `/api/v1/scrobble/oauth/anilist`.
2. The admin enters the client ID and client secret in **Settings → Integrations → Trackers**.
3. Press **Connect** on the Scrobble page and approve the login on AniList.

AniList tokens last about a year and cannot be refreshed. When the token expires, connect again.

AniList is one of two trackers with an anime list. The **Use anime for recommendation taste** switch lets Discover learn from it as well. See [Discover](/Maki/features/discover/) for the recommendation side.

## MyAnimeList

MyAnimeList also uses OAuth. Set up the client ID and secret in the same Trackers card, then press **Connect** on the Scrobble page. Maki refreshes the token on its own, so you do not need to reconnect. Like AniList, it has the anime list switch.

## Kitsu

Kitsu has no Connect step. Enter your Kitsu **Email** and **Password** in **Settings → Integrations → Trackers**. Maki signs in the first time it needs to talk to Kitsu.

## MangaBaka

MangaBaka uses a personal access token. Copy one from your MangaBaka settings and paste it into the **Personal Access Token** field in the Trackers card. No login step is needed.

## MangaUpdates

Enter your MangaUpdates **Username** and **Password** in the Trackers card. Maki signs in on first use.

MangaUpdates limits how often writes can be sent, so Maki batches them. An update can take a minute or so to show on mangaupdates.com. Entries on custom MangaUpdates lists are not imported.

## What Maki syncs

Every tracker has the same two switches on its block:

- **Scrobble reading** sends your progress. See [What counts as read](/Maki/guides/trackers/what-counts-as-read/) for when a chapter is sent.
- **Sync ratings** sends your Maki ratings.

Progress only moves forward. Maki never lowers a tracker's progress and never demotes a completed entry.

Each block also has an **Import ratings** button, which is enabled once the tracker is connected. It brings ratings from the tracker into Maki.

The Scrobble page runs a sync on a schedule, and an admin sets the interval. It shows when the last and next sync happened. Series Maki cannot match with confidence appear under **Needs review**. There you can **Assign** the series to a MangaBaka entry or **Ignore** it.

## Importing your lists

Import lists add the series on your tracker lists to your library. Connect the tracker first.

Open **Settings → Integrations → Import lists** and turn on the tracker's switch. Then choose:

- **Import entries with status.** Pick one or more of **Reading**, **Plan to read** and **Completed**. At least one must stay selected.
- **Root folder.** Where new series are filed. By default, it is the first folder you can see.
- **Monitor for new chapters** and **New chapters.** The new chapters option is **All**, **Smart** (only the next few ahead of your reading) or **Main only** (skips specials).
- **Max per run** caps how many entries one run handles.

Use **Run now** to start a sync, or **Sync everything** to pull the whole list again. Sync everything asks for confirmation first.

If your account cannot add series directly, Maki files each entry as a request for an admin to approve. Entries Maki could not match, or that you ignored, are listed under **Not matched or ignored**, where you can retry or un-ignore them.

To send unread series the other way, turn on **Add unread series as plan-to-read** in the Trackers card. Series with no progress are added to your tracker list as Plan to read. Entries already on your lists are never changed.
