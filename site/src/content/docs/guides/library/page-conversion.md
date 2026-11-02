---
title: WebP and AVIF conversion
description: Store chapter pages as WebP or AVIF to save space, which readers can open them, and how Maki converts new downloads and files already on disk.
---

Page conversion shrinks chapter files by re-encoding their pages as WebP or AVIF. It is off by
default. Turn it on under **Settings, Library, Page conversion**, then pick a format and a quality.
Maki converts new downloads as it places them, and a pausable pass works through files already on
disk.

## Which readers open which format

- **WebP** opens in every reader Maki works with: browsers, Kavita, Komga, KOReader, Mihon and
  Panels.
- **AVIF** does not open in KOReader or on iOS before 16.

If you read on KOReader or an iPhone, keep WebP. JPEG XL is not offered, because the image library
Maki bundles cannot write it and Kavita and KOReader cannot read it.

Clients that cannot decode a page's format can ask Maki for JPEG or PNG instead. See
[Reading over OPDS and Komga](/Maki/guides/reading/opds-apps/).

## Quality stops

There are five stops. **High** is the default. The wizard describes each one like this:

- **Smallest**: Screentone is smoothed away, lines stay intact.
- **Compact**: Screentone gets lighter but stays visible.
- **Balanced**: Tone is a little lighter. Fine at fit-to-screen.
- **High**: No difference we could see.
- **Lossless**: Identical pixels.

## Set it up with the wizard

**Set up conversion** on the Page conversion card opens a wizard with five steps: **Format**,
**Compare**, **What to convert**, **Review** and **Start**. Nothing is saved until you press a button
on the last step.

- **Format** picks WebP or AVIF, or Off.
- **Compare** shows one of your pages before and after at the stop you pick. Maki suggests a page
  from the middle of a file. **Pick another page** searches your series, and **Loupe, 2x** zooms in.
- **What to convert** holds the per-file rules, described below.
- **Review** estimates sizes and time by encoding a sample of the files each rule covers. Nothing
  changes, and **Re-estimate** runs it again.
- **Start** offers **Save and start**, which also starts the library pass, and **Save only**.

## Per-file rules and series settings

Rules are checked from the top, and the first one a file matches decides what happens to it. A rule
can match on **Kind of release**, **Source**, **Page format**, **Series type**, **Series tags**,
**Bits per pixel** or **File size**. Its action is **Convert at** one of the stops, or **Skip**. A
file no rule matches is converted at the stop set under **Everything else**.

An admin can also set one series' pages from its actions menu, under **Page conversion**: **Follow
the rules**, **Never**, or a stop. A series set to **Never** or to a fixed stop ignores the rules.
Files already converted stay as they are.

## What gets converted

JPEG pages become the chosen format, except at the Lossless stop, where they are kept as they are.
PNG pages become lossless WebP at the Lossless stop or with **Keep PNG pages lossless** on. At a
lossy stop they become the chosen format too.

Left as they are:

- Pages that are already WebP or AVIF, and animated pages.
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
**Undo conversion...** from its actions menu. The dialog says how many files still have their
original and which settings they were converted with. Then choose one of two options:

- **Restore the originals** puts the original pages back.
- **Restore and convert again with the current settings** converts the files again.

**Also set this series to Never** stops the series being converted again. It is unavailable with
**Restore and convert again with the current settings**. An original that housekeeping has deleted
cannot be restored. The Review step lists files converted earlier under **Converted earlier**, and
those are never converted again.

## Upgrades and quality

Upgrades and quality estimates judge a converted chapter by the size and format it was downloaded
in, so a converted file is never queued for an upgrade just because it got smaller. See
[Chapter upgrades and quality](/Maki/guides/library/chapter-upgrades/).
