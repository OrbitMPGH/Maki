---
title: Archiving a series
description: What archiving a finished series does, what it leaves alone, how to remove its files, and how to restore it to the library.
---

Archiving takes a finished series out of the active library. Maki stops checking its sources and
stops downloading for it, and the series moves to the **Archived** list. Chapters, reading history
and ratings all stay.

## Archive a series

Choose **Archive** from the series' actions menu and confirm.

- **Also remove the files** is off by default. Turn it on to delete the files and free the space
  shown next to it. It needs the permission to delete series, and is greyed out without it.
- If another reader is partway through the series, the dialog says so. Archiving stops new chapters
  for everyone, not only for you.
- Maki refuses to archive while something is still writing to the series, such as a running
  download or a source match. Try again once it finishes.

## What archiving does

- Stops checking the series' sources for new chapters.
- Cancels queued downloads and dismisses pending torrent proposals.
- Lists the series under **Archived**.

Archiving keeps:

- Every chapter row, with reading progress and ratings.
- The monitoring mode, so restoring brings the old mode back.
- Scrobbling of progress and ratings to trackers.

Archived series still count as a seed for recommendations.

## Remove files later

If the series kept its files, the archived banner on its page has a **Remove files** button. The
files go to the recycle bin and stay for the recycle bin retention period. See
[Retention and deleting files](/Maki/guides/library/retention/).

Removed chapters do not count as missing, and restoring the series can download them again.

## Restore a series

Use **Restore to library** on the series page, in its actions menu, or in the **Archived** list. It
needs the permission to archive series.

The series comes back with its old monitoring mode, and Maki refreshes it in the background.
Chapters released while it was archived are held and announced, not downloaded all at once, so you
can choose what to download.

If the files were removed, a dialog asks what to do. **Restore only** skips them. **Restore and
download** queues them again and needs the permission to download chapters.

:::tip
Archiving is reversible and leaves files alone unless you ask. If you are unsure whether a series is
finished, archive it and restore it later.
:::
