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

## What gets converted

JPEG pages become the chosen format. PNG pages become lossless WebP, or lossy too when the quality
is set to a lossy stop, unless you choose to keep them lossless.

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

## Upgrades and quality

Upgrades and quality estimates judge a converted chapter by the size and format it was downloaded
in, so a converted file is never queued for an upgrade just because it got smaller. See
[Chapter upgrades and quality](/Maki/guides/library/chapter-upgrades/).
