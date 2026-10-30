---
title: Maki's reader and shortcuts
description: Reading modes, reading profiles and the keyboard shortcuts in Maki's built-in reader.
---

For what the reader can do overall, see the [reader feature overview](/Maki/features/reader/).

## Reading modes

The toolbar's layout setting picks the mode:

- **Single page** (`1`)
- **Double page** (`2`), two pages side by side
- **Continuous** (`3`), one scrolling strip that suits webtoons

Other settings:

- **Direction**: right to left for manga or left to right (`D`).
- **Page fit**: **Fit height**, **Fit width**, **Fit screen** or **Original size (1:1)**.
- **Crop borders** (`C`, paged layouts only) trims plain white or black margins.
- **Wide pages** (single page layout only): shown whole, split into two pages, or rotated to fit.
- **Tap zones** (paged layouts only): click the screen edges to turn the page.

## Reading profiles

A reading profile is a saved set of these settings, managed under **Settings → Reading → Reader
profiles**. A profile can apply automatically to one or more series types. Types no profile covers
use **Default**, which you can edit too.

Maki picks the settings for a series in this order, and the first one that is set wins:

1. The override you saved for that series.
2. The profile you pinned to the series.
3. The profile for the series' type.
4. Your reader defaults.

The **Reading profile** control in the reader's settings popover shows which one is in use. It
offers **Auto** (labelled with the profile it picked, or **my defaults**), each of your profiles, and
**Just this series**. The line under it says what your next change will touch: this series only, the
profile in use, or your reader defaults.

The popover also has **Keep the screen awake** and **Incognito (don't record this session)**.

## Keyboard shortcuts

Press `?` in the reader for the list. Keys are ignored while focus is in a text field, and
combinations with Ctrl, Cmd or Alt are left to the browser.

| Key | Action |
| --- | --- |
| `→` | Next page. Reversed when reading right to left. |
| `←` | Previous page. Reversed when reading right to left. |
| `Space` | Next page. |
| `Shift` + `Space` | Previous page. |
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

In continuous mode, `Space` scrolls the strip as a normal page would, except on the last screen,
where it moves on.

:::tip
A button that has keyboard focus takes `Space` for itself. Click the page or move focus back to the
reader before using `Space` to turn pages.
:::
