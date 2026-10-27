---
title: Folder layout and naming
description: How Maki names series folders and chapter files, where root folders fit in, and what changing a format does to files already on disk.
---

This page is for anyone who wants to know where Maki puts files and what the names mean. Naming
is set in **Settings, Library, Folder & file naming**.

## Root folders

A root folder is a directory on the server that Maki owns for its library. Every series lives in
one root folder, and every file path Maki stores is relative to that root. Root folders are
managed in **Settings, Library, Root folders**. Access to them is granted per user under
**Settings, Users**, so someone with no grants sees an empty library.

Maki keeps its own bookkeeping inside each root folder: `.maki-trash/` for deleted and replaced
files, and `.maki/tmp` for work in progress. Folders starting with a dot are skipped by imports and
health scans.

## Series folders

A series gets one folder under its root. The default format is `{Series TitleYear}`, which gives
folders like `Berserk (1989)`. The year is what tells apart remakes that share a title. If the year
is unknown, the folder is just the title.

The format is set with **Series folder format**. Each series remembers the folder it was created
with, so changing the format affects new series only. Downloads for an existing series keep going
into its stored folder.

## Chapter files

The default chapter format is `{Series Title} {Chapter VolChap}{Chapter OneShotSuffix}`. Examples
from the defaults:

- `Berserk Vol.3 Ch.24.cbz`
- `One Punch Man Ch.10.5.cbz`
- `Look Back.cbz` (a one-shot with no chapter number)
- `Series - One-shot Title.cbz` (a one-shot that has a title)

Maki adds `.cbz` itself. A format cannot change the extension, so every file Maki builds opens in
the same readers.

Chapters in a language other than English get ` [xx]` after the name, such as `[es]`, unless the
format already has a `{Chapter Language}` token. Without that suffix, two languages of the same
chapter would write to the same file name.

A single file can hold several chapters, such as a volume compilation. Its name covers the whole
span: `Vol.1` when it holds every known chapter of that volume, `Vol.1 Ch.1-3` for part of a
volume, and `Ch.1-6` when there is no volume information.

### Tokens

Tokens are written in braces. The ones Maki understands are:

- Series: `{Series Title}`, `{Series TitleYear}`, `{Series CleanTitle}`, `{Series SortTitle}`,
  `{Series OriginalTitle}`, `{Series Year}`, `{Series Type}`
- Chapter: `{Chapter VolChap}`, `{Chapter Number}`, `{Chapter Volume}`, `{Chapter Title}`,
  `{Chapter OneShotSuffix}`, `{Chapter Language}`, `{Chapter Group}`
- Provider ids: `{MangaBakaId}`, `{MalId}`, `{AniListId}`, `{MangaDexId}`, `{MangaUpdatesId}`,
  `{KitsuId}`

Empty tokens print nothing. Maki then tidies the leftover spaces and empty brackets, so a token
without a value does not leave a gap in the name.

## Characters and length

Maki removes `< > : " / \ | ? *` and control characters from every name. Windows reserved names such
as `CON` get an underscore. A name longer than 200 bytes is shortened: the start and end are kept, with a
short hash in the middle so two long titles stay distinct.

## Changing a format

Saving a new format moves nothing on disk. Existing files keep their names until you rename them.
To rename everything, use **Rename every series to current format** on the same settings card.
Maki refuses to rename a series while a download for it is running, or when two chapters would end
up with the same name.

## Folder naming on import

When Maki imports a series folder that already exists on disk, **Folder naming on import** decides
what happens to it:

- **Rename the folder to the series folder format.** The default.
- **Keep the folder name, but put new downloads in a folder named by the format.** The old files
  stay where they are. New chapters go into a second folder, so one series can have files in two
  folders under the same root.
- **Keep the folder name, and put new downloads there too.**

:::tip
Pick a format before you import a large library. Renaming later works, but it touches every file.
:::
