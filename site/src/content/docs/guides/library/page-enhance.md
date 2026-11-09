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

- **Threads** sets how many CPU threads one page uses. A page is split into small tiles, and each
  thread works on one tile at a time. More than 10 threads adds nothing.
- **Cache size (MB)** caps the disk cache of upscaled pages, 1 GB by default. The oldest pages go
  first. **Clear cache** empties it at any time.
- **Test speed** times a built-in sample page and reports seconds per page, which is how long a
  reader waits for the server method.

Each model shows a speed and quality rating, and Maki 2x is marked Recommended.

Readers can skip chapters by file quality under **Don't enhance** in their reader settings. Skipped
chapters never reach the queue, so that work stays off the server. A reader who turns on **Whole
chapter** queues every page of the chapter they have open, so expect more work per reader with it on.

Pages over 16 megapixels are never upscaled on the server, since one can take around 2 GB of memory.
The reader uses a browser method for them.

:::tip
Upscaled pages are a disposable cache in the config folder. Clearing it only costs time the next
time a page is enhanced.
:::

## Fallbacks

While the server works on a page, the reader shows a browser method instead, chosen under **While
the server works** in the reader settings. Browser methods run on the reader's device and need
WebGL2. Without it, the page is shown plain.
