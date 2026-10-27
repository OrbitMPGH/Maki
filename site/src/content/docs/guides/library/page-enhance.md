---
title: Page enhance (upscaling)
description: Turn on server upscaling for the reader's Enhance setting, pick models, and check what it costs your server.
---

This page shows how to turn on server upscaling and what it needs from your server. For what Enhance
does and how readers use it, see [Page enhance](/Maki/features/page-enhance/).

## Turn it on

Server upscaling is an admin setting. Nothing runs until both steps are done.

1. Open **Settings, Library, Server upscaling** and switch on **Enable server upscaling**.
2. Pick the models readers may use. Models are not bundled with Maki. Each one downloads on first
   use from a Maki GitHub release, and Maki checks its SHA-256 before using it. Some models are for
   non-commercial use only. The licences are listed in
   `THIRD_PARTY_NOTICES.md` in the Maki repository.
3. Ask a reader to turn **Enhance** on, in the reader's settings popover. The **Method** list shows
   **Sharp (server)** and **Crisp line art (server)** once an admin has enabled them.

Readers still choose Off, Auto or Always for themselves. Enabling server upscaling does not change
anyone's reading.

## What the server needs

Upscaling runs on the Maki server, on the CPU. The Maki build ships the CPU build of ONNX Runtime,
and the upscaler has no GPU option. A page takes several seconds of server CPU. On a NAS it can take
a few times longer.

Use **Threads** to set how many CPU threads one page uses. Use **Cache size (MB)** to cap the disk
cache of upscaled pages. The oldest pages are dropped first once the cap is reached. The default cap
is 1 GB. **Clear cache** empties it at any time.

**Test speed** times one built-in sample page and reports the seconds per page on your hardware.
That is how long a reader waits for the server method.

:::tip
Upscaled pages are a disposable cache in the config folder. Clearing it only costs time the next
time a page is enhanced.
:::

## Fallbacks

While the server works on a page, the reader shows a browser method instead. Browser methods run on
the reader's own device and need WebGL2. The fallback is set under **While the server works** in the
reader settings. If a device has no WebGL2, the page is shown plain.
