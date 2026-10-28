---
title: Import an existing library
description: Bring CBZ folders that are already on disk into Maki, and how each folder gets matched to a series.
---

Use the **Import** page to bring comics you already have in folders, such as a Kavita or Komga
library, under Maki. Import reads the folders on disk, not the Kavita or Komga database.

Before you start:

- Add the library folder as a root folder under **Settings → Library → Root folders**.
- Install the local MangaBaka database, which the matching uses.

## Scan a root folder

1. Open **Import** and pick the root folder.
2. Click **Scan**. Maki lists each folder that holds comics. Folders with no comics are skipped.

Folders you ignored stay out of the list. The button above the list shows how many there are and
opens **Ignored folders**, where you can bring one back and scan again.

## How folders are matched

Maki strips release tags such as group names from the folder name and searches the local MangaBaka
database. It lists up to five matches and picks the first for a folder that is not in your library
yet.

- To choose another match, open the folder's match menu. Use **Find match** to search by title, or
  paste an id: a MangaBaka id or link, or an AniList or MyAnimeList link or id.
- Choose **- skip -** to leave a folder out.
- A folder whose series is already in your library shows **In library**. Importing it links its
  files into that series, so the missing chapters fill in.
- A folder for an archived series is a restore. Importing it brings the series back.

If nothing matches, the folder says **No metadata match found.** Use **Find match** or skip it.

## Import

Select the folders and click **Import N selected**. The confirmation dialog has a checkbox,
**Standardize ComicInfo.xml inside the imported files (recommended)**, which helps Kavita group these
files with later downloads. The preview button on a single folder shows what the import would do
before anything is written.

The dialog says folders are renamed to the English title, but the real behaviour comes from
**Settings → Library → Folder & file naming → Folder naming on import**:

- **Rename the folder to the series folder format.** This is the default.
- **Keep the folder name, but put new downloads in a folder named by the format.**
- **Keep the folder name, and put new downloads there too.**

Comics that are not CBZ, such as RAR or zip archives or a folder of loose images, are converted to
CBZ. Your originals stay where they are. PDFs are kept as they are and read in place.

## After the import

The import returns at once. Maki then searches the sources for each series in the background and
links your files to their chapters. Until that finishes, a folder reads **linking once sources are
found**. A file whose chapter number Maki cannot read is reported as unrecognized, and a file that
matches no chapter becomes a chapter of its own.

Every import is listed under **Recent imports** with **Undo this import**, available for 30 days.
Undo removes a series only when the import created it and nothing has been downloaded or read
since. It never deletes your original files.

:::tip
If Kavita shares this folder, see [Kavita alongside Maki](/Maki/guides/reading/kavita/) for how
Maki and Kavita keep each other up to date.
:::
