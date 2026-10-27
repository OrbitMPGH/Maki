---
title: What counts as read
description: When a chapter counts as read in Maki, and what each reading path sends to your trackers.
---

This page explains when a chapter counts as read, and so when it reaches your trackers. The rule is the same for every reading path. A chapter counts once Maki records it as completed, and the scrobbler sends the highest completed chapter number to each tracker.

## How a chapter reaches a tracker

Maki keeps one high-water mark per series, the highest chapter you have finished. The scrobbler reads that mark on each sync and sends it to every connected tracker that has the series. Three things follow from that:

- The number sent is the chapter number rounded down. Chapter 12.5 sends 12.
- The mark never goes down. If you mark chapters unread in Maki, your trackers keep their progress.
- Trackers only ever move forward, as described in [Connect a tracker](/Maki/guides/trackers/connect/).

Chapters you tick off as watched also move the mark, so a watched anime season updates your manga progress too.

## Maki reader

The built-in reader completes a chapter when you reach its last page. Opening a chapter and reading the first pages does not count.

Some chapters have a wide last page that is split in two. The chapter only completes once you have seen the second half.

## OPDS

OPDS apps read by fetching pages, so each page request is a progress signal.

Fetching the last page completes a chapter, with one exception. Many readers fetch the final page first to size their page bar. Maki ignores a last-page fetch when nothing has been read from that chapter yet. Once you have fetched any earlier page, the last page completes it as normal.

Earlier pages move your resume position but do not complete the chapter.

## KOReader

KOReader reports the page you reached, including when you read offline. Maki applies that to the chapters the file covers.

For a volume archive that holds several chapters, reaching a page completes every chapter before it and places you in the one it falls in. A chapter that shares the archive but has no chapter markers only completes on the last page, since Maki cannot tell how far into it you are. See [KOReader](/Maki/guides/reading/koreader/) for setup.

## Kavita

There are two ways Kavita reads reach Maki.

- A one-time **read status import** brings in chapters Kavita already marks as read. Those chapters reach your trackers on the next sync.
- **Live read sync** is opt-in and pulls your Kavita progress as you read. It records the read marks but does not update the high-water mark itself. The next scrobble sync reads the mark and sends it.

## Komga-compatible API

Apps that use the Komga API report progress with a page number and an optional completed flag.

- Setting the page to the last page completes the chapter.
- Sending `completed: true` completes it at any page.
- Sending `completed: false` never un-reads a finished chapter. Only a delete does that.

## One-shots

A one-shot is a single chapter with no number. It has no number to move the high-water mark to, so Maki counts it only once it is finished, and only when the tracker agrees the work is complete or has a single chapter. Until then, it is not sent as a finished read.

:::tip
If a chapter has not reached a tracker yet, check that it was completed, not only opened. Then wait for the next sync or press **Sync now** on the Scrobble page.
:::
