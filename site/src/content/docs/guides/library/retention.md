---
title: Retention and deleting files
description: What happens to files Maki deletes, replaces or converts, how long each one is kept, and how to restore or empty them.
---

Maki never deletes a file you asked it to remove straight away. Depending on why the file left the
library, it goes to one of three places, and each place keeps it for a set time.

| Where it goes | What puts it there | How long it stays |
| --- | --- | --- |
| Recycle bin | Deleting a file, removing chapters, deleting a series with its files, archiving with files removed | **Keep deleted files (days)**, default 14 |
| Upgrade trash | A chapter replaced by a better copy, a page conversion original, a file superseded by a torrent import | **Trash retention (days)**, default 14 |
| Gone for good | Automatic read file cleanup | No trash, deleted by the cleanup job |

Both trash folders live inside the root folder the file came from, in `.maki-trash/`. A file is moved
there by a rename, so it stays on the same disk and a hardlinked file stays hardlinked. Housekeeping
deletes whatever has been waiting longer than its setting.

## Recycle bin

Files you delete are kept in `.maki-trash/bin/<SeriesId>/`. Each one is recorded with its series, its
chapters and its original path, so it can be restored. Restores never overwrite a file that is already
there. If the series has moved to a new folder since, the file goes to the new folder.

The retention period is **Keep deleted files (days)** under **Settings, Library, Recycle bin**. Set it
to `0` to delete binned files on the next housekeeping pass. The same card has **Empty bin**. Entries
can be restored from Settings until they are deleted.

What each action does:

- **Delete file** on a chapter moves that file into the bin. The chapter stays in the list, marked as
  removed.
- **Remove chapters** deletes the chapter rows as well, and moves their files into the bin. Use it for a
  wrong source match.
- **Delete series** with files moves every file in the series' folders into the bin, then removes
  the folders it left empty. Maki never deletes a folder recursively. A file that another series
  claims is left where it is.

:::caution
A bin move needs the file and the bin on the same volume. If a root folder cannot give that, Maki
refuses the delete and offers to delete the files for good instead. Check that before you rely on the
bin.
:::

## Upgrade trash

When a better copy replaces a chapter, the old file goes to `.maki-trash/<SeriesId>/`, and a history
entry records the change. The same folder holds the original of each page conversion, and the files a
torrent import superseded. Maki keeps them until **Trash retention (days)** runs out. You will find the
setting under **Settings, Downloads, Upgrade scan**, and it defaults to 14.

Until housekeeping purges a replaced file, the replacement can be reverted.

## Read file cleanup

Read file cleanup is off by default. When an admin turns it on, Maki deletes a chapter's file a set
number of days after every reader who has progress in the series has finished it. Deleted files are not
moved to a trash folder. The chapter stays in the chapter list, marked **Auto-removed**.

Cleanup keeps the most recent finished chapter for each reader, and it skips a series while a download
is running for it.

## Disk space

Trash counts against the disk like any other file. If you need the space back, empty the bin, lower the
retention, or clear the upgrade trash by setting its retention to `0`.
