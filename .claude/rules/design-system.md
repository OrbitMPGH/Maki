---
paths:
  - "frontend/src/**"
---

# Design system

The look agreed in Sep 2026, replacing the near-black Mantine-default dashboard. Reference mockups
(series page, all four tabs, plus the type specimen and the menu/dialog sheet):
https://claude.ai/code/artifact/78d7e845-900e-431b-a723-528fd5eeb21d

Deliberately broad `paths`: this loads on any frontend edit, because the point is that new surfaces
match the old ones without anyone having to remember to look.

The direction in one line: **content forward, one accent, colour only where it carries meaning.**
The thing being managed (the cover, the title, the counts) is loud; the chrome is quiet.

## Type

Two faces, both self-hosted in `frontend/public/fonts/` with `@font-face` in `theme.css`.
**Never a Google Fonts `<link>`.** Maki runs on LANs with no outbound access, and a CDN fetch leaks
every user's IP to a third party on every page load. `theme.ts` already named Inter without anything
loading it, so before this redesign the whole app was rendering in whatever `ui-sans-serif` resolved
to. Don't reintroduce that: if you name a face, ship the file.

- **Anton** is the display face. Uppercase only, and only for the identity of the thing on screen.
  Never for body copy, buttons, labels, table headers or numbers. It has one weight and no italic;
  do not fake either. On a hero page it is always the title. On a working page it depends on what
  the title is doing. `PageHeader`'s `face` prop is the switch, and `display` is the default:

  - `display` (Anton): Library, Home, Discover, Stats, Creator. Places you arrive at.
  - `face="text"` (Inter 700, 28px): Settings, Activity, Requests, Notifications, Import,
    Add series, Scrobble. Forms and logs, where a 34px poster face is just loud.

  A new page picks its side by that question, not by taste.
- **Inter** is everything else, weights 400/500/600/700.

| Role | Face | Size / line-height | Weight | Colour |
| --- | --- | --- | --- | --- |
| Hero title | Anton | 78 / 0.94, `letter-spacing: .012em` | 400 | `--ink-hi` |
| Page title, headline pages | Anton | 34 / 1.0 | 400 | `--ink-hi` |
| Page title, form and log pages | Inter | 28 / 1.15, `-0.02em` | 700 | `--ink-hi` |
| Panel / section title | Inter | 17 / 1.3, `-0.01em` | 600 | `--ink` |
| Sub-heading inside a panel | Inter | 14 / 1.4 | 600 | `--ink` |
| Body copy | Inter | 14.5 / 1.66 | 400 | `--ink-3` |
| UI default (buttons, cells, menu items) | Inter | 13.5 / 1.4 | 400–600 | `--ink-2` |
| Label | Inter | 12.5 / 1.4 | 400 | `--ink-4` |
| Table header | Inter | 11.5 / 1.2 | 600 | `--ink-4` |
| Pill | Inter | 11 / 1, `letter-spacing: .03em` | 700 | semantic |
| Nav section label | Inter | 10 / 1, `letter-spacing: .14em`, uppercase | 700 | `--ink-5` |
| Display number | Inter | 26–34, tabular | 600 | `--ink-hi` |

**Uppercase letterspaced micro-labels are now limited to exactly two places: nav section headers and
pills.** They were on every label on the old series page and that density is most of what made it
read as generated. A label is sentence case at 12.5px.

Every count, ratio, date or size gets `.tnum` (already in `theme.css`). Columns of numbers that
jitter are the fastest way to make a table look unfinished.

## Colour

### Ground

Neutral, very slightly warm, four steps of surface and five of ink. Every one of these needs a
matching value under `[data-theme='light']` in the same commit; a token that only exists in dark
silently renders as `initial` on the light preset.

The light preset survives, as warm paper rather than blue-white, and its ramp is already in
`theme.css`. It is a phase behind dark on purpose: the hero backdrop still needs a light recipe of
its own, because scrims tuned for a near-black ground turn to mud on paper. Until that exists,
build and check dark first, then confirm nothing in light is unreadable.

| Token | Dark | Use |
| --- | --- | --- |
| `--app-bg` | `#0c0c0d` | page ground |
| `--surface` | `#141416` | panels, cards |
| `--surface-2` | `#17171a` | table headers, inputs, active nav |
| `--surface-3` | `#1a1a1d` | secondary buttons |
| `--overlay` | `#16161a` | menus, modals, popovers |
| `--border` | `#212124` | panel border |
| `--border-strong` | `#26262a` | control border |
| `--hairline` | `#1b1b1e` | row separators inside a panel |
| `--ink-hi` | `#f2efe9` | headings, the cream note in the palette |
| `--ink` | `#e2dfd8` | emphasised body |
| `--ink-2` | `#d3d0ca` | default text |
| `--ink-3` | `#a9a6a0` | secondary text |
| `--ink-4` | `#76767d` | labels, table headers |
| `--ink-5` | `#56565c` | disabled, faint |

### Accent

One accent, from the user's theme preset, driving `--brand` and Mantine's `brand` exactly as today.
Default preset is **crimson**. The accent means two things and nothing else:

1. the primary action on the surface (there is one per screen), and
2. the current thing (active nav item, active page in a pager, selected rows).

It is not a decoration. If you reach for it a third time on one screen, one of the three is not
actually primary.

Six presets, all cut to the same weight (white label at 5:1 or better as a solid fill) so the app
feels like one product whichever is picked. `--brand-fg` is the light tint for accent-coloured
*text* on a panel, which the solid value is far too dark for.

| Preset | `--brand` | `--brand-fg` | Note |
| --- | --- | --- | --- |
| crimson (default) | `#b3302a` | `#e8837a` | The 5.6 house colour. |
| rose | `#b02f56` | `#e88ba6` | The old `#f52069` re-cut; that one is fluorescent against cream ink. |
| plum | `#8b3f8e` | `#cf90d1` | Furthest from any reserved hue. |
| iris | `#5a56cf` | `#a2a0ee` | Replaces indigo, which was too light to hold white text. |
| cobalt | `#2c6ab5` | `#86b0e6` | The quiet one. |
| teal | `#0f7a75` | `#6cc4be` | Replaces emerald. |

**Green and amber are not available as accents.** Mint green is `--ok` and gold is `--warn` plus the
rating stars, so an emerald or amber accent turns a colour that carries meaning into decoration. The
old `emerald` and `amber` preset ids are retired; keep `rose` and `indigo` as ids so stored
`maki-theme` values still resolve (indigo's *value* becomes iris).

`--gold #e0a93a` never moves with the preset: rating stars and the active tab underline. It is the
only colour in the system that ignores the accent.

**Crimson and `--danger` share a hue**, which is the one real weakness of a red accent. They stay
apart by form, not colour: the accent is only ever a solid fill, `--danger` is only ever light text
on a 16% tint. The single exception is a confirm dialog's destructive button, `#8c2620`, which is
deliberately duller than any accent.

### Semantic

Reserved. Each means one thing, everywhere, forever.

| Token | Text | Fill | Means |
| --- | --- | --- | --- |
| `--ok` | `#5fc98c` | `rgba(63,166,106,.15)` | on disk, linked, read, synced, completed |
| `--warn` | `#e0a93a` | `rgba(224,169,58,.15)` | needs a decision from the user |
| `--info` | `#8fa6e0` | `rgba(90,140,220,.16)` | in flight, in progress |
| `--danger` | `#e08078` | `rgba(200,70,60,.16)` | failed, missing from disk, destructive |
| `--watched` | `#b39ae8` | `rgba(150,110,220,.16)` | watched, not read |
| neutral | `#a0a0a8` | `rgba(140,140,150,.13)` | known but inert: missing, queued, a source name |

**Hard rule: more than two coloured pills in one row is a bug.** Publication status, content rating,
type, year and genres are facts, not states, and they render as a plain grey text line under the
title. A pill is for something the user might act on.

`components/ui/status.tsx` is the only place a status colour is chosen. If you are writing
`color="teal"` on a `Badge` in a page file, stop and add a visual to `status.tsx` instead.

## Surfaces and depth

Three levels, no more: page ground, panel, overlay. Panels never nest inside panels; a panel that
needs internal grouping uses a `--hairline` divider and a sub-heading.

- Panel: `background: var(--surface); border: 1px solid var(--border); border-radius: 14px`. No
  shadow. Shadow is what separates an overlay from the page, so spending it on a panel removes the
  only cue overlays have.
- Overlay (menu, modal, popover): `--overlay`, `--border-strong`, radius 15,
  `box-shadow: 0 30px 70px -20px rgba(0,0,0,.9)`.
- Radii: 6 pill, 8 icon action, 9 control, 11 primary button, 14 panel, 15 overlay.

## Density

Fixed heights, so tables from different sections line up when they sit on the same page.

- Table rows: 48 chapters, 56 sources, 50 files. Header cell 44. Cell padding 16 (dense tables) or
  18 (roomy ones).
- Controls: 46 hero action, 40 full-width input, 34 section-level button, 30 in-row control,
  28 icon action.
- Panel padding 22. Gap between panels 22. Gap between page sections 26.

## Page anatomy

Two shapes. Pick one; do not invent a third.

**Hero page** for a single entity (series, creator, and any future one-of-a-kind page). A 452px
backdrop band, the poster sitting inside it, the title block to its right, one primary action plus
at most two secondaries plus an overflow, and the tab strip along the bottom of the band. Everything
else lives in panels below.

**Working page** for lists and dashboards (library, activity, requests, queue, stats, settings).
`PageHeader` with an Anton title, a toolbar row, then panels. No backdrop, no poster.

### No top bar on desktop

Tried and reverted. A bar with no surface over a hero backdrop is a 58px strip of nothing that
still pushes every page down, and it reads as chrome someone forgot to delete. Search, downloads,
notifications and health live in the rail instead: search as a full-width field under the wordmark,
the three status icons in a row at the foot beside the account.

The header survives only below `sm`, where it carries the burger, so `--app-shell-header-offset` is
0 on desktop and 58 on mobile. Anything that clears the bar has to read that variable rather than
assume a height.

### The backdrop recipe

Four stacked absolute layers inside the band, in this order. Do not substitute a single dark overlay:
a flat wash is what made the old hero look like a smudge.

```css
/* 1. the art: the series poster, filled to the band */
inset: -7%;
background-size: cover;
background-position: 50% 16%;
filter: saturate(.95) contrast(1.05) brightness(.82) blur(2px);

/* 2. corner falloff, keeps the light in one place */
background: radial-gradient(120% 150% at 88% 10%, rgba(12,12,13,0) 0%, rgba(12,12,13,.55) 62%, rgba(12,12,13,.9) 100%);

/* 3. horizontal scrim, buys contrast for the title */
background: linear-gradient(90deg, var(--app-bg) 13%, rgba(12,12,13,.88) 34%, rgba(12,12,13,.46) 62%, rgba(12,12,13,.18) 100%);

/* 4. vertical scrim, dissolves the band into the page */
background: linear-gradient(180deg, rgba(12,12,13,.4) 0%, rgba(12,12,13,.02) 24%, rgba(12,12,13,.68) 70%, var(--app-bg) 100%);
```

A series with no cover gets layer 1 skipped and layers 2 to 4 over `--surface`. Do not fall back to
a generated gradient; an empty band reads as deliberate, a mauve mesh reads as filler.

### Tabs

Only when a page has three or more sections that people visit separately, and only on a hero page.
A tab has to earn itself: Sources was a tab for exactly one commit before it turned out to fit under
Progress in the Details column, where it is visible without a click and the right column stops being
half empty. Prefer a panel in a column over a tab whenever the content fits.

**Tab state lives in the URL** (`?tab=chapters`), so a refresh, a back button and a shared link all
land where the user was, and an unrecognised value falls back rather than rendering no panel. Mantine `Tabs` is already used in Discover, Settings and Stats; match it.

Active tab: `--ink-hi` text, 2px `--gold` bottom border. Inactive: `--ink-4`, transparent border.
A count next to the label is `--ink-5`, weight 500.

## Component vocabulary

Reach for one of these before inventing anything.

- **Pill** for a state, from `status.tsx`. 22px, radius 6, weight 700, 11px, icon optional.
- **Chip** for a value the user set or can set (genre, tag, a select rendered as a control). 26px,
  radius 7, `--surface-3`, `--border-strong`, weight 500. A dashed chip is the "add" affordance.
- **Record row** for read-only metadata: a 128px label column, a value column, `--hairline` on top.
  This replaces the old badge-soup approach to metadata.
- **Switch, stepper, segmented control, icon action** keep their Mantine components; only the tokens
  change.
- **Panel header**: title left, count or hint next to it in `--ink-4`, actions right at 34px.
- **Long lists of provider data** (MangaBaka returns 130+ tags on a popular series) cap at ~14 with
  a `+N more` toggle. A wall of chips buries everything under it and none of it is why anyone
  opened the page.

## What not to do

Every item here is something the old series page did, and together they are what "AI-generated" was
naming:

- A rainbow of pills in one row.
- Eight buttons of equal weight in a row. There is one primary; the long tail goes in an overflow
  menu grouped by what it touches.
- A blurred cover behind a flat dark veil as the whole backdrop treatment.
- Uppercase letterspaced 10px labels on every field.
- Decorative gradient meshes in the page background.
- A card around everything, including single values.

## Working on this

- Put component defaults in `theme.ts` (`Card.extend`, `Button.extend`, ...) rather than repeating
  props at every call site. A prop repeated three times is a default that has not been set yet.
- Tokens go in `theme.css` `:root`, and in the same edit under `[data-theme='light']`.
- Adding or re-cutting an accent preset means three files: `accents` in `theme.ts`,
  `[data-accent='...']` in `theme.css`, `THEME_PRESETS` in `theme-context.tsx`. Preset **ids** are
  stored in the `maki-theme` localStorage key, so changing an id logs every existing user out of
  their choice. Change the value, keep the id.
- Contrast floor is 4.5:1 for anything under 18px. `--ink-4` on `--surface` is the lowest that
  passes; `--ink-5` is for disabled text and decoration only, never for something worth reading.
- Type-check with `npx tsc -b`, never `npx tsc --noEmit` (see root CLAUDE.md).
