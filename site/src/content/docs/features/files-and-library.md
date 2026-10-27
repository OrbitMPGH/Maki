---
title: Files and library
description: Output layout, page conversion, the library view, health checks and Mihon import.
---

## Output

The default layout is `{Series}/{Series} Vol.X Ch.Y.cbz`, with configurable folder and file naming.
ComicInfo.xml carries series, chapter number, volume, authors, genres, language and reading
direction. Files are written atomically, so a reader never sees a half-written CBZ. Maki can also
write a `cover.jpg` into each series folder for readers that look for one.

## Page conversion

Off by default (Settings, Library, Page conversion). Pick WebP or AVIF and a quality. New downloads
are stored with their JPEG pages in that format and their PNG pages as lossless WebP.

A pausable library pass converts the files already on disk, one at a time, and shows what it saved.
Several pages of a file are encoded at once; the card sets how many (by default half the cores, at
most 4).

**Skipped on purpose**

- A page or file that would not come out at least 5% smaller.
- WebP and AVIF pages, animations, pages under 16 KB and pages over 16,383 pixels on a side.
- Files hardlinked to a torrent client's copy.

Originals wait in the upgrade trash (`.maki-trash/<series id>/conv-*`) for the trash retention
period. The pass pauses itself rather than leave less than 1 GB free.

**Compatibility.** WebP opens in every reader Maki works with (browsers, Kavita, Komga, KOReader,
Mihon, Panels). AVIF does not open in KOReader or on iOS before 16. JPEG XL is not offered: the
image library Maki bundles cannot write it, and Kavita and KOReader cannot read it.

Upgrades, source quality estimates and the quality shown on the series page keep judging a
converted chapter by the size and format it was downloaded in, so converting never makes a copy
look like a worse release.

## Library view

Grid or list view with density options. Shows download state, read badges and monitor status.
Filter by source, chapter count or user tag, save filters, and set Wanted in bulk. Per-series
incognito mode keeps a title out of stats and progress without hiding it.

## Library health

Admins can index and verify archives, and find missing, unreadable, duplicate or suspicious files.
Repeated findings of one kind fold into one row with **Acknowledge all**. Replacement and deletion
operations are reviewed before they run, and unlinked CBZs can be imported.

## Mihon backup import

Upload a Mihon backup (`.tachibk` or `.proto.gz`) on the Import page to add its library. Entries
are matched by tracker ids, then by title, with a review table for fixes. Categories become tags
and read chapters are marked read.
