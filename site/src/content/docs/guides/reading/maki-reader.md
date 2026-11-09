---
title: Maki's reader and shortcuts
description: Reading modes, reading profiles and the keyboard shortcuts in Maki's built-in reader.
---

For what the reader can do overall, see the [reader feature overview](/Maki/features/reader/). For
notes on pages, see [Chapter notes](/Maki/guides/reading/chapter-notes/).

## Reader settings

<!-- screenshot: the reader settings panel -->

The gear in the bottom bar opens the quick settings panel. At the top sit the reading profile in use
and **Incognito**. Under them are the settings you change while reading: the layout, direction, page
fit, a brightness slider and four colour filters (none, gray, sepia, invert), each shown on the page
you are on. The rows under them show their
current value and open a page of their own:

- **Wide pages**, in the single page layout only.
- **Page look**: the background (dark or OLED black) and **Crop borders**.
- **Enhance**, see below.
- **While reading**: tap zones, the page number, showing the chapter name when a new chapter
  opens, auto-advance to the next chapter, scrolling webtoon chapters, **Notes beside the page** and
  **Keep the screen awake**.

Each page opens in place with a back row at the top. `Esc` on one of those pages goes back to the
first one, and `Esc` there closes the panel.

## Reading modes

The layout setting picks the mode:

- **Single page** (`1`)
- **Double page** (`2`), two pages side by side
- **Continuous** (`3`), one scrolling strip that suits webtoons

Other settings:

- **Direction**: right to left for manga or left to right (`D`).
- **Page fit**: **Fit height**, **Fit width**, **Fit screen** or **Original size (1:1)**.
- **Crop borders** (`C`, paged layouts only) trims plain white or black margins.
- **Wide pages** (single page layout only): shown whole, split into two pages, or rotated to fit.
- **Tap zones**: in the paged layouts, click the screen edges to turn the page. In Continuous, see
  below.

## Continuous mode

With tap zones on, a tap on the bottom third of the screen moves the strip down most of a screen,
and a tap on the top third moves it back. A strip of what you just read stays at the top so you keep
your place. `Space` and `Page Down` do the same, and `Shift` + `Space` and `Page Up` go back.

The auto-scroll button in the toolbar (or `A`) scrolls the strip for you, at one of ten speeds (`[`
slower, `]` faster). It pauses while your finger is on the screen or the toolbar is up, picks up
again after you scroll by hand, and carries on into the next chapter when auto-advance is on.

At the end of a chapter, keep scrolling (or pull, on touch) to open the next one. The prompt names
the chapter and fills a ring as you pull.

## Page bar

<!-- screenshot: the page bar with a thumbnail above a segment -->

The bar along the bottom has one segment per page. Segments up to the page on screen are lit.
Pages ahead that the server has already upscaled show in a lighter grey. Hover a segment to see its
thumbnail. Click or drag to jump; the page changes when you let go. In right to left reading, page 1
is the rightmost segment. Press `T` to open the page thumbnails strip.

## Enhance

<!-- screenshot: the reader settings panel with the Enhance page open -->

Enhance sharpens soft pages. Its controls are on the **Enhance** page of the settings panel:
**Enhance** (Off, Auto or Always), then **Method** and **Don't enhance** once Enhance is on. **While
the server works**, **Upscale progress** and **Whole chapter** appear only with a server method. Hold `H`, or the compare button in the top bar, to see
the original page. See [Page enhance](/Maki/features/page-enhance/).

## Reading profiles

A reading profile is a saved set of these settings, managed under **Settings → Reading → Reader
profiles**. A profile can apply automatically to one or more series types. Types no profile covers
use **Default**, which you can edit too.

Maki picks the settings for a series in this order, and the first one that is set wins:

1. The override you saved for that series.
2. The profile you pinned to the series.
3. The profile for the series' type.
4. Your reader defaults.

The chip at the top of the reader's settings panel shows which one is in use. Click it to change
it: the list offers **Auto** (labelled with the profile it picked, or **my defaults**), each of your
profiles, and **Just this series**. The line under it says what your next change will touch: this
series only, the profile in use, or your reader defaults.

**Incognito**, next to the profile chip, stops recording the current session.

## Keyboard shortcuts

Press `?` in the reader for the list. Keys are ignored while focus is in a text field, and
combinations with Ctrl, Cmd or Alt are left to the browser.

| Key | Action |
| --- | --- |
| `→` | Next page. Reversed when reading right to left. |
| `←` | Previous page. Reversed when reading right to left. |
| `Space` | Next page. In Continuous, down a screen. |
| `Shift` + `Space` | Previous page. In Continuous, up a screen. |
| `Page Down` / `Page Up` | In Continuous, down or up a screen |
| `A` | Auto-scroll on or off, in Continuous |
| `[` / `]` | Auto-scroll slower / faster |
| `Home` / `End` | First page / last page |
| `1`, `2`, `3` | Single page, double page, continuous |
| `D` | Switch reading direction |
| `C` | Crop borders on or off |
| `+` (or `=`) / `-` | Zoom in / zoom out |
| `0` | Reset zoom |
| `F` | Full screen |
| `T` | Page thumbnails |
| `B` | Bookmark this page |
| `H` (hold) | Show the original page while the key is held, if enhancement is on |
| `Esc` | Back to the series page. Outside full screen only, since `Esc` leaves full screen first. |
| `?` | Show or hide the shortcut list |

:::tip
A button that has keyboard focus takes `Space` for itself. Click the page or move focus back to the
reader before using `Space` to turn pages.
:::
