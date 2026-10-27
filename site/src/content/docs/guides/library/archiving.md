---
title: Archiving a series
description: What archiving a finished series does, what it leaves alone, how to remove its files, and how to restore it to the library.
---

Archiving takes a finished series out of the active library. Maki stops checking its sources and
stops downloading for it, and the series moves to the **Archived** list. Nothing else changes. Its
chapters, reading history and rating all stay.

## Archive a series

Open the series and choose **Archive** from its actions menu. A dialog asks you to confirm.

The dialog has one extra option: **Also remove the files**. Leave it off to keep the files on disk.
Turn it on to remove them, which frees the space shown next to the option. Removing files needs the
permission to delete series. Without it, the option is greyed out.

If another reader is partway through the series, the dialog says so. Archiving stops new chapters
for everyone, not only for you.

Maki refuses to archive a series while something is still writing to it, such as a running
download or a source match. Try again once that finishes.

## What archiving does

- Maki stops checking the series' sources for new chapters.
- Downloads queued for the series are cancelled, and pending torrent proposals for it are dismissed.
- The series is listed under **Archived** in the library.

Archiving keeps:

- Every chapter row, along with reading progress and ratings.
- The series' monitoring mode, so restoring it brings the old mode back.
- Scrobbling of progress and ratings to trackers.

Archived series still count as a seed for recommendations.

## Remove files later

An archived series that kept its files can have them removed later. The archived banner on the
series page has a **Remove files** button. The files go to the recycle bin, which keeps them for the recycle bin retention period before they are
deleted. See [Retention and deleting files](/Maki/guides/library/retention/).

Removed chapters do not count as missing. Restoring the series can download them again.

## Restore a series

Restore an archived series from the **Restore to library** button on its page, from the actions menu,
or from the **Archived** list. Restoring needs the permission to archive series.

Restoring puts the series back with its old monitoring mode, and Maki refreshes it in the background.
Chapters released while the series was archived are held and announced, not downloaded all at once.
If that many chapters arrive together, Maki holds them so you can choose what to download.

If the files were removed, a dialog asks whether to download the removed chapters again. **Restore
only** brings the series back without downloading them. **Restore and download** also queues them,
which needs the permission to download chapters.

:::tip
Archive is reversible and does not touch files unless you ask it to. If you are unsure whether a
series is finished, archive it and restore it later.
:::
