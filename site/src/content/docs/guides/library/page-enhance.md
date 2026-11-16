---
title: Page enhance (upscaling)
description: Turn on server upscaling for the reader's Enhance setting, pick models, and check what it costs your server.
---

This page covers the admin side of server upscaling. For what Enhance does and how readers use it,
see [Page enhance](/Maki/features/page-enhance/).

## Turn it on

Nothing runs until both steps are done.

1. Open **Settings, Library, Server upscaling** and switch on **Enable server upscaling**.
2. Pick the models readers may use. Models are not bundled. Each downloads from a Maki
   GitHub release when you tick it and save, or from its Download button, and Maki checks its
   SHA-256 before using it. Some models are non-commercial only,
   and the licences are listed in `THIRD_PARTY_NOTICES.md` in the Maki repository.

Readers then turn **Enhance** on under **Enhance** in the reader's settings panel, where the **Method** list shows
**Maki 2x**, **Real-ESRGAN** and **SPAN-S** under "On the server". They still choose Off, Auto or Always for
themselves, so enabling the feature changes nobody's reading.

## What the server needs

Upscaling runs on the server CPU. Maki ships the CPU build of ONNX Runtime and has no GPU option. A
page takes several seconds of CPU, and a few times longer on a NAS.

- **Threads** sets how many CPU threads one page uses, up to the number of cores (the default is half
  the cores, at most 4). A page is split into small tiles, and each thread works on one tile at a
  time.
- **JPEG XL for colour pages**, on by default, saves upscaled colour pages as JPEG XL, so dark
  gradients stay smooth on an OLED screen and files are about 14% smaller. A browser that cannot
  show JPEG XL, such as Edge, gets WebP for that page instead. Grey pages are always WebP.
- **Cache size (MB)** caps the disk cache of upscaled pages, 1 GB by default. The oldest pages go
  first. **Clear cache** empties it at any time.
- **Test speed** times a built-in sample page and reports seconds per page, which is how long a
  reader waits for the server method.

Each model shows a speed and quality rating, and Maki 2x is marked Recommended.

Readers can skip chapters by file quality under **Don't enhance** in their reader settings. Skipped
chapters never reach the queue, so that work stays off the server. A reader who turns on **Whole
chapter** queues every page of the chapter they have open and then the whole next chapter, so expect
more work per reader with it on.

## Prepare chapters ahead

**Prepare chapters ahead**, off by default, lets the server upscale while nobody is reading. It takes
the next chapter of each series a reader has set to Reading and read in the last 14 days, up to 8
series per reader, so they open sharp even on a phone. Only series whose reader settings have
Enhance on Always with a server model count.

- **Read within (days)**, **Series per reader**, **Chapters per series** (1 to 3) and **Space (MB)**
  (4 GB by default, apart from the cache size) are adjustable.
- Prepared pages stay until the chapter is read.
- Reading always comes first: a prepared page in progress stops the moment someone opens a page.
- Preparing runs on half the upscaling threads, so it takes about half the CPU and does not slow the
  page someone is reading.

Anyone who can edit a series can override this under **Automation** in its actions menu: follow
Settings, never prepare it, or keep 1 to 20 chapters ready, for a series read in long sittings. A
series with its own number is prepared however long ago it was last read and does not count toward
the series limit.

Pages over 16 megapixels are never upscaled on the server, since one can take around 2 GB of memory.
The reader uses a browser method for them.

Pages upscaled with Maki 2x before v0.37.0 are made again the next time they are read, because of a
fix for faint boxes on dark panels and a higher quality for colour pages.

:::tip
Upscaled pages are a disposable cache in the config folder. Clearing it only costs time the next
time a page is enhanced.
:::

## Fallbacks

While the server works on a page, the reader shows a browser method instead, chosen under **While
the server works** in the reader settings. Browser methods run on the reader's device and need
WebGL2. Without it, the page is shown plain.
