---
title: Retention and deleting files
description: What happens to files Maki deletes, replaces or converts, how long each one is kept, and how to restore or empty them.
---

Maki never deletes a file you asked it to remove straight away. Depending on why the file left the
library, it goes to one of three places.

| Where it goes | What puts it there | How long it stays |
| --- | --- | --- |
| Recycle bin | Deleting a file, removing chapters, deleting a series with its files, archiving with files removed | **Keep deleted files (days)**, default 14 |
| Upgrade trash | A chapter replaced by a better copy, a page conversion original, a file superseded by a torrent import | **Trash retention (days)**, default 14 |
| Gone for good | Automatic read file cleanup | No trash, deleted by the cleanup job |

Both trash folders live in `.maki-trash/` inside the root folder the file came from. Files move there
by rename, so they stay on the same disk and a hardlinked file stays hardlinked. Housekeeping deletes
whatever has waited longer than its setting.

## Recycle bin

Deleted files go to `.maki-trash/bin/<SeriesId>/`, recorded with their series, chapters and original
path. Restore entries from Settings until they are deleted. Restores never overwrite an existing
file, and if the series has moved to a new folder, the file goes to the new one.

Set **Keep deleted files (days)** under **Settings, Library, Recycle bin**. `0` deletes binned files
on the next housekeeping pass. **Empty bin** is on the same card.

- **Delete file** on a chapter moves the file into the bin. The chapter stays in the list with a **File
  removed** badge.
- **Remove chapters** deletes the chapter rows and moves their files into the bin. Use it for a wrong
  source match.
- **Delete series** with files moves every file in the series' folders into the bin, then removes
  the folders it left empty. Maki never deletes a folder recursively, and a file that another series
  claims stays where it is.

:::caution
A bin move needs the file and the bin on the same volume. If a root folder cannot give that, Maki
refuses the delete and offers to delete the files for good instead.
:::

## Upgrade trash

When a better copy replaces a chapter, the old file goes to `.maki-trash/<SeriesId>/` and a history
entry records the change. The same folder holds page conversion originals and files a torrent import
superseded. They stay for **Trash retention (days)**, set under **Settings, Downloads, Upgrade
scan** (default 14). Until housekeeping purges a replaced file, the replacement can be reverted.

## Read file cleanup

Off by default. An admin turns it on in the **Clean up read chapters** card on the Downloads tab,
where the delay defaults to 7 days. Maki deletes a chapter's file that many days after every reader
who has progress in the series has finished it. The file is deleted outright, not trashed, and the
chapter stays in the list marked **Auto-removed**.

Until the deletion, the chapter shows **Removed in N days** and gets no upgrades. Cleanup unticks
**Wanted** on the chapter, so Maki does not download it again. Tick **Wanted** again to queue it.

Cleanup keeps each reader's most recent finished chapter, and skips a series while a download is
running for it.

## Disk space

Trash counts against the disk like any other file. To get the space back, empty the bin, lower the
retention, or set the upgrade trash retention to `0`.
