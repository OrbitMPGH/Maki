---
title: CBZ and ComicInfo.xml
description: What goes inside each chapter file Maki writes, and which ComicInfo.xml fields it fills in.
---

Each chapter Maki downloads becomes one CBZ, a zip archive with a `ComicInfo.xml` at its root. This
page helps if you read the files in another app, or want to know why a field looks wrong in Kavita
or another reader.

## What is inside

- `ComicInfo.xml` at the root.
- One image per page, named `001.jpg`, `002.jpg` and so on. Names have at least three digits, so
  readers that sort names as text still get the pages in order.

Page images are stored without compression, since JPEG and PNG are already compressed. Maki writes
the archive to a `.partial` file and moves it into place when complete, so a reader never opens a
half-written file.

The series folder may also get a `cover.jpg` beside the CBZ files, not inside them.

## ComicInfo.xml fields

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

`Manga` follows the series type:

| Series type | Value |
| --- | --- |
| Manga | `YesAndRightToLeft` |
| Manhwa, manhua | `Yes` (left-to-right with manga-style reading) |
| OEL and other types | `No` |

A type Maki does not recognize gets the manga default.

`AgeRating` maps the content rating: safe becomes `Teen`, suggestive becomes `Mature 17+`, erotica
becomes `Adults Only 18+`, and pornographic becomes `X18+`. Safe maps to Teen because Maki's ratings
say nothing about violence.

## Files Maki adopts

Files Maki did not download, such as library imports or finished torrents, can get their
ComicInfo.xml rewritten:

- Series-level fields (title, summary, authors, genres) are standardized to Maki's metadata.
- Chapter-level fields such as number and title are kept, or filled in from the linked chapter.
- Fields Maki has no opinion on, such as translator or characters, and unknown elements stay as
  they were.

Torrent imports rewrite ComicInfo.xml only when every file was copied. A hardlinked file is left
alone, because a rewrite would replace the shared file with a full copy and undo the hardlink.
