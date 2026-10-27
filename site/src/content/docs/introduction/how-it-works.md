---
title: How it works
description: The path a chapter takes from a source site to the app you read it in.
---

Maki is one server with a web interface. It does four jobs.

## 1. Find

You add a series by searching the local copy of the [MangaBaka](https://mangabaka.org) database.
One match identifies the series and brings along its MyAnimeList, AniList, MangaUpdates and Kitsu
IDs. Maki then links the series to the [sources](/Maki/guides/sources/built-in-sources/) that carry it.

## 2. Watch

Maki checks your sources on a schedule and picks the best one for each series. Series that
release often are checked every 30 minutes. New chapters are queued on their own, and you can
follow the queue under **Activity**.

## 3. Save

Each chapter is downloaded page by page and written as a CBZ file with ComicInfo.xml metadata
into your library folder. The files are plain and portable, so Kavita, KOReader, Panels, Chunky,
Mihon and other comic readers can open the same folder.

## 4. Read and track

Read in Maki's own reader, over [OPDS and the Komga-compatible API](/Maki/guides/reading/opds-apps/),
or in [KOReader](/Maki/guides/reading/koreader/). Progress from every app is merged and sent to
your trackers.

## Where the data lives

- `/config` volume: database, logs, covers, page cache and `config.json`.
- `/library` volume: chapters.

Back up both. See [Backup and restore](/Maki/guides/running/backup-restore/).
