---
title: How progress syncs between apps
description: What happens to read state, Stats and your trackers when you read in Maki, over OPDS, in KOReader, in Kavita or in a Komga app.
---

Maki keeps one read state per series. Each app you read in writes progress into it, takes progress
from it, or both.

| Where you read | What reaches Maki | What goes back out |
| --- | --- | --- |
| Maki's reader | Your position, and each chapter you finish | Trackers, and Kavita if you turn on the Kavita push switch |
| OPDS apps | Each page saves your position. A chapter completes on its last page once you have opened it | Trackers and Stats, like any other read |
| KOReader | Page positions, including reads made offline | Your Maki position, when you open the book in KOReader |
| Kavita | Read state for series that match, through the scrobble check and the live switch | Trackers. Maki's reads reach Kavita only if the push switch is on |
| Komga-compatible apps | Progress the app reports | Read state, so the app shows the same chapters as read |

## Reading in Maki

Progress is saved as you read. Time is counted only while the tab is visible and in focus, and it
stops after five minutes with no input. Offline progress is kept on the device and sent when the
connection comes back. Reads show up in Stats and Rewind as they happen.

## OPDS apps

Each page an app loads counts as read, if **Track reading progress from OPDS** is on under **Settings
→ Reading → OPDS catalogue**. Many apps load the last page early to size their page bar, so Maki
does not count that alone as a read. Turn the switch off if an app reports progress you did not make.

See [Reading over OPDS](/Maki/guides/reading/opds-apps/) for setup.

## KOReader

KOReader sends your position as you read and asks Maki for the latest position when a book opens.
Reads made offline are sent when it reconnects. Setup and the file matching rules are in
[KOReader](/Maki/guides/reading/koreader/).

## Kavita

Reads in Kavita reach Maki in two ways:

- The scrobble check, which runs with your trackers and reads Kavita's progress for each matched series.
- **Mark chapters read here as soon as they're read in Kavita**, which is optional and works live.

Maki's reads reach Kavita only with **Mark chapters read in Kavita too** on. Both switches are under
**Settings → Reading → Kavita read sync**. See [Kavita alongside Maki](/Maki/guides/reading/kavita/).

## Komga-compatible apps

Apps that use Maki's Komga API, including Mihon's Komga tracker, send progress to Maki and get read
state back. One chapter reported as read counts as a read. A jump over several chapters marks them
read in one go. See [Komga-compatible apps](/Maki/guides/reading/komga-apps/).

## Trackers

Maki sends progress to AniList, MyAnimeList, Kitsu, MangaBaka and MangaUpdates. Connect your accounts
on the Scrobble page. Each tracker's card under **Settings → Integrations** has a **Scrobble reading**
switch, so you can turn one tracker off without disconnecting it.

Maki sends progress every 30 minutes by default. **Sync now** on the Scrobble page sends it straight
away. Progress on a tracker is never lowered.

## Incognito

Each series has an incognito setting:

- **Full** keeps the reading position and nothing else. The series does not appear in Stats.
- **No scrobble** keeps the series out of tracker sync.

## When two apps disagree

Your saved page can move back, for example when you reread an earlier page. A chapter that is finished
stays finished when an app reports an earlier page. Only marking it unread in Maki clears that.

If you mark a chapter unread in Maki, an offline read from before that mark does not bring it back.

## Stats and Rewind

Reads you make in Maki or in an app count in Stats and Rewind on the day you read them. Read state
brought in by an import, such as a Mihon backup or a one-off Kavita import, is not dated, so it does
not show up as reading on a particular day. See [Stats and Rewind](/Maki/guides/running/stats-and-rewind/).
