---
title: CBZ and ComicInfo.xml
description: What goes inside each chapter file Maki writes, and which ComicInfo.xml fields it fills in.
---

This page is for anyone reading Maki's files in another app, or checking why a field looks wrong in
Kavita or another reader. Each chapter Maki downloads becomes one CBZ, which is a zip archive with
a `ComicInfo.xml` at its root.

## What is inside

- `ComicInfo.xml` at the root of the archive.
- One image per page, named `001.jpg`, `002.jpg`, and so on. The number has at least three digits,
  and more only when the chapter has more pages. Readers that sort names as text still get the
  pages in order.

Page images are stored without compression, since JPEG and PNG are already compressed. The archive
is written to a `.partial` file first and moved into place once it is complete, so a reader never
opens a half-written file.

The series folder may also get a `cover.jpg`. That file sits beside the CBZ files and is not inside
them.

## ComicInfo.xml fields

Maki fills in the following fields from its own metadata:

| Field | Source |
| --- | --- |
| `Series` | The series' English title |
| `LocalizedSeries` | An alt title in the chapter's language, else the original title |
| `Title` | The chapter title. If there is none, `Chapter N`, or the series title for a chapter without a number |
| `Number` | The chapter number |
| `Volume` | The chapter's volume, when the source gives one |
| `Count` | The series' total chapters, only once the series is completed |
| `Summary` | The series overview |
| `Year`, `Month`, `Day` | The chapter's release date, when known |
| `Writer` | Story authors |
| `Penciller` | Art authors |
| `Publisher` | Publisher |
| `Genre` | Series genres |
| `Tags` | Series tags from metadata |
| `Web` | Links to the series on MangaBaka, AniList, MyAnimeList, and the other sites Maki knows |
| `LanguageISO` | The chapter's language |
| `Manga` | Reading direction, from the series type (see below) |
| `AgeRating` | From the series content rating (see below) |
| `ScanInformation` | The scanlation group, when there is one |
| `PageCount` | Number of pages in the chapter |

`Manga` follows the series type. Manga is `YesAndRightToLeft`. Manhwa and manhua are `Yes`, which
means left-to-right with manga-style reading. OEL and other types are `No`. A type Maki does not
recognize gets the manga default.

`AgeRating` maps the content rating: safe becomes `Teen`, suggestive becomes `Mature 17+`, erotica
becomes `Adults Only 18+`, and pornographic becomes `X18+`. The safe rating maps to Teen because
Maki's ratings say nothing about violence.

## Files Maki adopts

Files that Maki did not download, such as files from a library import or a finished torrent, can
get their ComicInfo.xml rewritten. Maki standardizes the series-level fields (title, summary,
authors, genres) to its own metadata. Chapter-level fields such as the number and title are kept,
or filled in from the linked chapter. Fields Maki has no opinion on, such as translator or
characters, stay as they were. Unknown elements are carried over unchanged.

Torrent imports rewrite ComicInfo.xml only when every file was copied. A hardlinked file is left
alone, because a rewrite would replace the shared file with a full copy and undo the hardlink.
