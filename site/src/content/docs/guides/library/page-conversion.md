---
title: Page conversion
description: Store chapter pages as WebP, AVIF or JPEG XL to save space, split tall webtoon pages, which readers can open each format, and how Maki converts new downloads and files already on disk.
---

Page conversion shrinks chapter files by re-encoding their pages as WebP, AVIF or JPEG XL. It is off
by default. Turn it on under **Settings, Library, Page conversion** and click **Set up conversion**.
New downloads, upgrades and torrent imports go through a background queue after they land, so
downloads finish without waiting on the encoder. A file waits in the queue while someone is reading
it, and the Page conversion card shows how many are queued. A pausable pass works through files
already on disk.

## Which readers open which format

- **WebP** opens in every reader Maki works with: browsers, Kavita, Komga, KOReader, Mihon and
  Panels.
- **AVIF** makes the smallest files. It does not open in KOReader or on iOS before 16.
- **JPEG XL** is about the size of WebP (a tenth smaller at High) and converts about three times
  faster. Maki's reader shows it as it is in browsers that support JPEG XL and as WebP everywhere
  else, and OPDS, Komga and KOReader always get WebP pages. A CBZ you download keeps its JPEG XL
  pages, which KOReader and most comic apps cannot open.

If you read downloaded files on KOReader or an iPhone, keep WebP. JPEG XL needs libjxl, which the
Docker image and the Windows and Linux builds include.

Clients that cannot decode a page's format can ask Maki for JPEG or PNG instead. See
[Reading over OPDS and Komga](/Maki/guides/reading/opds-apps/).

## Quality stops

There are five stops. **High** is the default. For black-and-white pages each one is described like
this:

- **Smallest**: Screentone is smoothed away, lines stay intact.
- **Compact**: Screentone gets lighter but stays visible.
- **Balanced**: Tone is a little lighter. Fine at fit-to-screen.
- **High**: No difference we could see.
- **Lossless**: Identical pixels.

Colour pages have no screentone, so their descriptions talk about skies, skin and dark gradients
instead. At **High** a colour page is hard to tell from the original.

## Set it up

**Set up conversion** opens one page that holds the whole plan. Nothing is saved until you press
**Save only**, or **Save and start**, which also starts the library pass.

The plan is a table. Each row is a rule, and the last row, **Files no rule above matches**, is the
default. Every row has two targets, a format and a quality for **Black-and-white pages** and one for
**Colour pages**, so colour inserts can go to JPEG XL while the manga around them goes to AVIF. A
colour target can also be **Same as black-and-white**.

Click a target to see it on one of your own pages of that kind, taken from the files that row
covers, with the page's size in the other formats beside it. **Fit** and **100%** set the zoom, and
**Pick another page** chooses a different one. Each row shows how big its files would end up, and
the total for the library updates as you edit.

## Rules and series settings

Rules are checked from the top, and the first one a file matches decides what happens to it. A rule
can match on **Kind of release**, **Source**, **Page format**, **Series type**, **Series tags**,
**Bits per pixel** or **File size**. **Matching files are** sets whether the files it matches are
**Converted**, at that row's targets, or **Skipped**. **Start from a template** offers common rules.

An admin can also set one series' pages from its actions menu, under **Files → Page conversion**:
**Follow the rules**, **Never**, or a stop. A series set to **Never** or to a fixed stop ignores the rules.
Files already converted stay as they are.

## Split tall pages

Very tall webtoon pages are hard on e-readers and KOReader. Turn on **Split very tall pages** and set
**Split pages taller than** to cut them into several shorter pages, in the blank gaps between panels
where there are any. Only strips at least two and a half times as tall as they are wide are split.
The setup shows where the cuts land on the tallest page in your library.

Splitting applies to new downloads and, through the library pass, to files you already have,
including ones that are already WebP or were converted before. Reading positions, bookmarks and page
notes move with their page, and undoing a conversion puts them back.

## What gets converted

JPEG pages become the chosen format, except at the Lossless stop, where they are kept as they are.
PNG pages become lossless WebP at the Lossless stop or with **Keep PNG pages lossless** on. At a
lossy stop they become the chosen format too.

Left as they are:

- Pages that are already WebP, AVIF or JPEG XL, animated pages, and pages with more than 8 bits
  per channel.
- Pages under 16 KB, and pages over 16,383 pixels on a side.
- A page or file that would not come out at least 5% smaller.
- Files hardlinked to a torrent client's copy. Rewriting one would store a second copy beside the
  seeding one.
- PDF files, which are read in place.
- Any series with a download in progress.

## The library pass

The pass converts files already on disk one at a time, shows how much it has saved, and can be
paused. It also pauses itself rather than leave less than 1 GB free on the disk. Several pages of a
file are encoded at once. The number is set on the same card and defaults to half your cores, between
2 and 4.

Each original goes to `.maki-trash/<SeriesId>/conv-*` and stays for the trash retention period
(default 14 days), so you can undo a conversion until housekeeping deletes it.

## Undo a conversion

Once a series has converted files with originals in the trash, an admin can open
**Files → Undo conversion...** from its actions menu. The dialog says how many files still have their
original and which settings they were converted with. Then choose one of two options:

- **Restore the originals** puts the original pages back.
- **Restore and convert again with the current settings** converts the files again.

**Also set this series to Never** stops the series being converted again. It is unavailable with
**Restore and convert again with the current settings**. An original that housekeeping has deleted
cannot be restored. Files converted earlier are listed under **Converted earlier**, and those are
never converted again.

## Upgrades and quality

Upgrades and quality estimates judge a converted chapter by the size and format it was downloaded
in, so a converted file is never queued for an upgrade just because it got smaller. See
[Chapter upgrades and quality](/Maki/guides/library/chapter-upgrades/).
