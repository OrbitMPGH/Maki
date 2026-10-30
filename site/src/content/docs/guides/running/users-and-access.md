---
title: Users and library access
description: Per-user data, library access and Kavita.
---

Each account has its own reading history, preferences and tracker connections. The library is shared:
one copy of the files and one set of series and chapters, so a second reader costs no disk.

- **Per user:** read state and resume position, bookmarks, ratings, reader defaults and per-series
  overrides, saved filters, start page, Home layout, title language, series-page rails,
  content-rating ceiling, OPDS feed, and the tracker accounts (AniList, MyAnimeList, Kitsu,
  MangaUpdates, MangaBaka) your progress is sent to. Ratings land on *your* tracker profile.
- **Shared, admin-only:** root folders, download clients and indexers, sources and their priority,
  metadata and recommendation settings, notifications, backups, and tracker app registrations. The
  client ID and secret are shared, but each person connects their own account.

## Library access

Access is granted per root folder under **Settings → Users & security**. An account with no grants
sees an empty library. Series, chapters, covers, search, OPDS and KOReader sync all respect it, and
the same grants decide where someone with the **Add series** permission may add a title.

## Single-user mode

While only one account is enabled, Maki hides requests, leaderboards and the other multi-user parts.
It switches off by itself when a second account is enabled. The switch is at the top of
**Settings → Users & security**.

## Kavita

Kavita is one server with one API key, so everything it reports is a single person's reading.
**Settings → Integrations → Kavita**, field **Attribute Kavita's reading to**, chooses which Maki
account that is (unset means the first admin). Only that account can sync read status with Kavita.
