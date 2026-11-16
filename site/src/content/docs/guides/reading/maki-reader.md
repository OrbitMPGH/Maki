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
- **Page look**: the background (dark, OLED black, white, grey, or **Auto**, which takes the colour
  of the page's edge and eases between pages) and **Crop borders**.
- **Enhance**, see below. **Method** is a row that opens a list of the methods on this device and on
  the server, each with a line on what it does or why it cannot be picked yet.
- **Chapters**: showing the chapter name when a new chapter opens, auto-advance to the next chapter,
  **Skip recaps**, **Hide credit pages** and scrolling webtoon chapters.
- **While reading**: tap zones, the page number, **Notes beside the page**, **Progress rail** and
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
- **Tap zones**: in the paged layouts, tap or click the screen to turn the page. There are four
  layouts: **Sides** (back on one side, forward on the other), **L-shape**, **Kindle** (most of the
  page goes forward) and **Edges** (both edges go forward, for one hand). The zone size can be
  changed and back and forward swapped, and while you adjust them the zones are drawn on the page.
  In Continuous, the zone size sets the height of the top and bottom bands, see below.

In the Single and Double layouts you can also:

- **Pinch to zoom** around the point between your fingers. Ctrl and the scroll wheel, or a trackpad
  pinch, zoom the same way. While zoomed, drag with a finger or the mouse to move around the page.
  Double-tap zooms in on the spot you tapped.
- **Swipe** sideways to turn the page, following the reading direction. Mostly vertical drags are
  ignored, and a zoomed page pans instead.
- **Turn pages with the scroll wheel.** A page taller than the screen scrolls first, then turns from
  the bottom, and scrolling up from the top goes back. A trackpad swipe turns one page at most.

The mouse cursor hides after a couple of seconds at rest over the page.

## Continuous mode

With tap zones on, a tap on the bottom third of the screen moves the strip down most of a screen,
and a tap on the top third moves it back. A strip of what you just read stays at the top so you keep
your place. `Space` and `Page Down` do the same, and `Shift` + `Space` and `Page Up` go back.

The auto-scroll button in the toolbar (or `A`) scrolls the strip for you, at one of ten speeds (`[`
slower, `]` faster). It pauses while your finger is on the screen or the toolbar is up, picks up
again after you scroll by hand, and carries on into the next chapter when auto-advance is on. A
reader left on auto-scroll with nobody there stops counting reading time and lets the screen sleep
after five idle minutes.

**Progress rail**, under While reading, shows the chapter's progress as a thin line down the right
edge. With the bars up you can drag it to move through the chapter. Reopening a chapter brings you
back to where you stopped inside a tall slice, not to the top of that slice, on any device.

Webtoon chapters that open by repeating the end of the previous chapter get a **Skip recap** button,
which jumps to where the new pages start. **Skip recaps** under Chapters does it on its own.

**Hide credit pages**, also under Chapters, leaves out scanlator credit, recruitment, donation and
notice pages at the start and end of a chapter, and a group's banner joined to the top of a webtoon
slice. Maki looks for pages that repeat at the same edge of nearby chapters and reads their text, so
copyright pages, "to be continued" cards, title pages and covers stay. Your files are never changed,
finishing the last page shown still counts the chapter as read, and the reader settings show how many
pages were left out with a button to show them again. An admin can have Maki find credit pages for
the whole library in the background under **Settings → Library → Credit pages**.

## End of a chapter

At the end of a chapter, keep scrolling (or pull, on touch) to open the next one. The prompt names
the chapter and fills a ring as you pull. With auto-advance off, the end screen shows instead:

- About how much reading time is left in the series at your pace. Hover the page counter while you
  read for the same figure plus the rest of the chapter.
- On the last chapter of a series you have finished, it says so and asks for your final rating, with
  a one-tap **Keep** if you gave it a score so far.
- When you are caught up, a rough span for the next chapter, such as "usually comes in 1 to 2
  weeks", with how sure the estimate is. Never a date.
- If the next chapter finishes downloading while the screen is open, **Read** appears. Queued
  chapters show their download progress.

## Page bar

<!-- screenshot: the page bar with a thumbnail above a segment -->

The bar along the bottom has one segment per page. Segments up to the page on screen are lit.
Pages ahead that the server has already upscaled show in a lighter grey. Hover a segment to see its
thumbnail. Click or drag to jump; the page changes when you let go. In right to left reading, page 1
is the rightmost segment. Bookmarked pages are flagged on the bar. Press `T` to open the page
thumbnails strip.

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
| `E` | Enhance on Always, or back to what you had |
| `+` (or `=`) / `-` | Zoom in / zoom out |
| `0` | Reset zoom |
| `F` | Full screen |
| `T` | Page thumbnails |
| `B` | Bookmark this page |
| `,` / `.` | Previous / next bookmark |
| `H` (hold) | Show the original page while the key is held, if enhancement is on |
| `Esc` | Back to the page you opened the chapter from, such as Home or the Library, or the series page after a reload or a link. Outside full screen only, since `Esc` leaves full screen first. |
| `?` | Show or hide the shortcut list |

:::tip
A button that has keyboard focus takes `Space` for itself. Click the page or move focus back to the
reader before using `Space` to turn pages.
:::
