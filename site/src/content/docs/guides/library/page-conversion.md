---
title: WebP and AVIF conversion
description: Store chapter pages as WebP or AVIF to save space, which readers can open them, and how Maki converts new downloads and files already on disk.
---

Page conversion makes chapter files smaller by re-encoding their pages as WebP or AVIF. It is off by
default. Turn it on under **Settings, Library, Page conversion**. Pick a format and a quality, and
Maki converts new downloads as it places them. A pausable pass works through the files already on
disk.

## Which readers open which format

Pick a format your readers can open. The apps Maki works with do not all handle the two formats
the same way:

- **WebP** opens in every reader Maki works with: browsers, Kavita, Komga, KOReader, Mihon, and
  Panels.
- **AVIF** does not open in KOReader, and it does not open on iOS before version 16.

If you read on KOReader or an iPhone, keep the format on WebP. JPEG XL is not offered, because the
image library Maki bundles cannot write it, and Kavita and KOReader cannot read it.

Clients that cannot decode a page's format can ask Maki for JPEG or PNG instead. See
[Reading over OPDS and Komga](/Maki/guides/reading/opds-apps/).

## What gets converted

New downloads are stored with their JPEG pages in the chosen format. Their PNG pages become lossless
WebP. When the quality is set to a lossy stop, PNG pages are encoded lossy too, unless you choose
to keep them lossless.

Some things are left as they are:

- Pages that are already WebP or AVIF, and animated pages.
- Pages under 16 KB, and pages over 16,383 pixels on a side.
- A page or file that would not come out at least 5% smaller.
- Files hardlinked to a torrent client's copy. Rewriting one would store a second copy beside the
  seeding one.
- PDF files, which are read in place.
- Any series with a download in progress.

## The library pass

The library pass converts files already on disk, one at a time. It shows how much it has saved, and
you can pause it. The pass also pauses itself rather than leave less than 1 GB free on the disk.
Several pages of a file are encoded at once. The number is set on the same card, and by default it
is half your cores, with a maximum of 4.

Each original goes to the trash for the series, in `.maki-trash/<SeriesId>/conv-*`. The original
stays there for the trash retention period, which defaults to 14 days. Until then you can undo a
conversion. After that housekeeping deletes the original.

## Upgrades and quality

Conversion does not make a copy look worse. Upgrades and quality estimates judge a converted chapter
by the size and format it was downloaded in. A converted file is never queued for an upgrade just
because it got smaller. See [Chapter upgrades and quality](/Maki/guides/library/chapter-upgrades/).

:::caution
AVIF pages do not open in KOReader or on iOS before 16. If any of your readers are on that list,
use WebP.
:::
