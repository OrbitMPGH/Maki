---
title: Folder layout and naming
description: How Maki names series folders and chapter files, where root folders fit in, and what changing a format does to files already on disk.
---

Naming is set in **Settings, Library, Folder & file naming**.

## Root folders

A root folder is a directory on the server that Maki owns for its library. Every series lives in one
root folder, and every stored file path is relative to it. Manage them in **Settings, Library, Root
folders**. Access is granted per user under **Settings, Users**, so someone with no grants sees an
empty library.

Maki keeps `.maki-trash/` (deleted and replaced files) and `.maki/tmp` (work in progress) inside each
root folder. Imports and health scans skip folders starting with a dot.

## Series folders

A series gets one folder under its root. The default format, `{Series TitleYear}`, gives folders like
`Berserk (1989)`. The year tells apart remakes that share a title, and is left out when unknown.

Each series remembers the folder it was created with. Changing **Series folder format** affects new
series only, and existing series keep downloading into their stored folder.

## Chapter files

The default chapter format is `{Series Title} {Chapter VolChap}{Chapter OneShotSuffix}`. It gives:

- `Berserk Vol.3 Ch.24.cbz`
- `One Punch Man Ch.10.5.cbz`
- `Look Back.cbz` (a one-shot with no chapter number)
- `Series - One-shot Title.cbz` (a one-shot that has a title)

Maki adds `.cbz` itself, and a format cannot change it.

Chapters in a language other than English get ` [xx]` after the name, such as `[es]`, unless the
format already has a `{Chapter Language}` token. Otherwise two languages of the same chapter would
write to the same file name.

A file that holds several chapters, such as a volume compilation, is named for the whole span:
`Vol.1` when it holds every known chapter of that volume, `Vol.1 Ch.1-3` for part of a volume, and
`Ch.1-6` when there is no volume information.

### Tokens

- Series: `{Series Title}`, `{Series TitleYear}`, `{Series CleanTitle}`, `{Series SortTitle}`,
  `{Series OriginalTitle}`, `{Series Year}`, `{Series Type}`
- Chapter: `{Chapter VolChap}`, `{Chapter Number}`, `{Chapter Volume}`, `{Chapter Title}`,
  `{Chapter OneShotSuffix}`, `{Chapter Language}`, `{Chapter Group}`
- Provider ids: `{MangaBakaId}`, `{MalId}`, `{AniListId}`, `{MangaDexId}`, `{MangaUpdatesId}`,
  `{KitsuId}`

Empty tokens print nothing, and Maki tidies leftover spaces and empty brackets.

## Characters and length

Maki removes `< > : " / \ | ? *` and control characters from every name, and gives Windows reserved
names such as `CON` an underscore. A name over 200 bytes is shortened: Maki keeps the start and end
with a short hash in the middle, so two long titles stay distinct.

## Changing a format

Saving a new format moves nothing. Existing files keep their names until you use **Rename every
series to current format** on the same card. Maki refuses to rename a series while a download for it
is running, or when two chapters would end up with the same name.

## Folder naming on import

When Maki imports a series folder that already exists, **Folder naming on import** decides what
happens:

- **Rename the folder to the series folder format.** The default.
- **Keep the folder name, but put new downloads in a folder named by the format.** Old files stay
  put and new chapters go into a second folder, so one series can span two folders under the same
  root.
- **Keep the folder name, and put new downloads there too.**

:::tip
Pick a format before you import a large library. Renaming later works, but it touches every file.
:::
