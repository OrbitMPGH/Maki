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

To archive several series, select them in the Library and choose **Archive** from the selection
bar. The dialog has the same **Also remove the files** choice. Maki then reports how many were
archived and the first refusal. If the recycle bin refuses some files, the dialog stays open for
those series and offers to delete them for good.

Archiving a part-read series from its page asks whether to rate it first, if it has no rating yet.
The score you pick becomes the rating when the archive goes through. If you already set a so-far
score, the dialog shows it. See [Ratings](/Maki/guides/trackers/ratings/).

<!-- screenshot: the Archive dialog for a part-read series, with the rating stars -->

When you finish a completed series, its page offers to archive it with the line **You've finished
this. Archive it?**. It needs the permission to archive series, and it only appears once everyone
else who has started the series has finished it too. Dismiss it with the X and it stays hidden on
all your devices.

## What archiving does

- Stops checking the series' sources for new chapters.
- Cancels queued downloads and dismisses pending torrent proposals.
- Lists the series under **Archived**, and leaves it out of the Library view and its counts.
- Hides it from Home rails, OPDS and Komga.

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

Restore is also offered elsewhere. On Discover, an archived series shows an **Archived** badge with
**Restore** in place of adding it. If you approve a request for an archived series, the error
offers **Restore** too.

The series comes back with its old monitoring mode, and Maki refreshes it in the background.
Chapters released while it was archived are held and announced, not downloaded all at once, so you
can choose what to download.

If no chapter file is left and some were removed, a dialog asks what to do. **Restore only** skips
them. **Restore and download** queues them again and needs the permission to download chapters.

:::tip
Archiving is reversible and leaves files alone unless you ask. If you are unsure whether a series is
finished, archive it and restore it later.
:::
