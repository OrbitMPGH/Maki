---
title: What counts as read
description: When a chapter counts as read in Maki, and what each reading path sends to your trackers.
---

A chapter counts once Maki records it as completed, and the scrobbler sends the highest completed
chapter number to each tracker. The rule is the same for every reading path.

## How a chapter reaches a tracker

Maki keeps one high-water mark per series, the highest chapter you have finished. Each sync sends
that mark to every connected tracker that has the series.

- The number sent is the chapter number rounded down. Chapter 12.5 sends 12.
- The mark never goes down. Marking chapters unread in Maki leaves your trackers' progress alone.
- Trackers only move forward, as described in [Connect a tracker](/Maki/guides/trackers/connect/).
- Chapters you tick off as watched also move the mark, so a watched anime season updates your manga
  progress too.

## Maki reader

The reader completes a chapter when you reach its last page. Opening it and reading the first pages
does not count. If the last page is a wide spread split in two, the chapter completes once you have
seen the second half.

## OPDS

OPDS apps read by fetching pages, so each page request is a progress signal. Earlier pages move your
resume position but do not complete the chapter. Fetching the last page does, with one exception:
many readers fetch the final page first to size their page bar, so Maki ignores a last-page fetch
when nothing has been read from that chapter yet.

## KOReader

KOReader reports the page you reached, including when you read offline, and Maki applies that to the
chapters the file covers. In a volume archive holding several chapters, reaching a page completes
every chapter before it and places you in the one it falls in. A chapter in the archive with no
chapter markers only completes on the last page, since Maki cannot tell how far into it you are. See
[KOReader](/Maki/guides/reading/koreader/) for setup.

## Kavita

- A one-time **read status import** brings in chapters Kavita already marks as read. They reach your
  trackers on the next sync.
- **Live read sync** is opt-in and pulls your Kavita progress as you read. It records read marks but
  does not move the high-water mark itself. The next scrobble sync does that.

## Komga-compatible API

Apps using the Komga API report a page number and an optional completed flag.

- Setting the page to the last page completes the chapter.
- `completed: true` completes it at any page.
- `completed: false` never un-reads a finished chapter. Only a delete does that.

## One-shots

A one-shot is a single chapter with no number, so there is nothing to move the high-water mark to.
Maki counts it only once it is finished, and only when the tracker agrees the work is complete or has
a single chapter. Until then it is not sent as a finished read.

:::tip
If a chapter has not reached a tracker, check that it was completed, not only opened. Then wait for
the next sync or press **Sync now** on the Scrobble page.
:::
