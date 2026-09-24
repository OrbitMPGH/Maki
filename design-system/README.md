Maki is a self-hosted manga collection manager: add a series once and it keeps it complete. The interface is content-first and cinematic: a near-black ground, cover art as the hero, one accent colour, and everything else quiet so the covers carry the colour.

## Voice and copy

- Write plain, direct sentences, the way a developer explains something to a teammate. Say what happens: "Add series", "Refresh the catalogue", "Browse the catalogue", never "Submit" or "OK".
- Sentence case everywhere: buttons, titles, menu items. Uppercase only through the `label` style (figure labels, stat labels, cover badges), and let CSS do it.
- Address the reader as "you" and the product as "Maki": "Maki finds its chapters and keeps it complete." No "we".
- Empty states are statements, not apologies: "No series yet", "No chapters read in this period.", "Queue is empty". Follow with one line on what would fill it and at most one action.
- Explain a consequence in a short clause after a colon: "Sliding: a failed sign-in resets the timer." "No change: this title was already in that state."
- No em dashes. No emoji in UI copy. No filler words ("seamless", "robust", "leverage") and no "it's not just X, it's Y".
- Every string is translated (Maki ships in fourteen languages). A status Maki has no word for is shown exactly as the server sent it rather than guessed at.

## Colour

**One accent, near-black ground.** `brand` is the only accent. It marks progress, selection, focus and the one primary action. Everything else is surfaces and ink. Covers bring their own colour; the chrome stays out of their way.

**Themes.** Five presets, matching the app's picker: `Indigo` (the default dark scheme), `Light`, and three dark accent swaps, `Rose`, `Emerald` and `Amber`. The accent themes change only the brand group (`brand`, `brand-hover`, `brand-on`, `brand-fg`, `brand-fg-on-art`, `brand-glow`, `primary`, `primary-hover`, `primary-on`); surfaces, ink and status stay those of dark. Light keeps the indigo accent.

**The layer ladder.** Build every screen from these, in order of height:
- `app-bg`: the page ground. In dark, `body` also carries two faint radial washes of brand indigo at the top corners (12% and 10%); that is the only gradient in the chrome.
- `surface`: the default layer. Every Card, Paper and `Panel` sits here.
- `surface-2`: one step up. Tag chips, inputs at rest.
- `surface-raised`: overlays (modals, drawers, menus, popovers). Dark elevates by lightening (it aliases `surface-2`); light elevates by staying white and casting `shadow-raised`. Never assume one direction reads in both.
- `surface-sunken`: recessed wells. `app-bg` in dark, grey `#eceef6` in light.
- `surface-hover` for hover fills on rows and chips.

Borders are 1px `border`; hovered or emphasised edges take `border-strong`; row dividers inside tables use `hairline`.

**Ink.** Five steps, each 4.5:1 or better on `surface` in both schemes. `ink-hi` for titles, figures and the active item; `ink` for body copy; `ink-2` for secondary copy (chip labels, status words); `ink-3` for descriptions and meta lines (Mantine's dimmed colour maps here); `ink-4` for the quietest labels and zero counts.

**Status.** Semantic hues live in tokens, never in Mantine's stock palette, so "Completed" is the same green on a cover, a badge and a table row and follows the light theme: `ok`, `warn`, `info`, `danger`, `watched` (read state, kept apart from brand so a read row never looks selected), `suggestive` (olive, for the Suggestive rating and the 65 to 79 score band), `neutral` (unknown or idle). Each has a `-soft` partner at 15% for a tinted fill behind it. Two fills carry their own foreground: `ok-on` for icons on an `ok` fill, and `danger-fill`, a deeper red for filled danger buttons so their white labels hold 5.6:1. Status is always carried by a word or an icon as well as the hue.

Content ratings run green to red on purpose: Safe `ok`, Suggestive `suggestive`, Erotica `warn`, Pornographic `danger`. Catalogue scores band the same way: 80 and up `ok`, 65 to 79 `suggestive`, 50 to 64 `warn`, below 50 `danger`.

**Other palettes.** `rating` is the star colour. `tier-1` to `tier-6` (with `-soft` fills) colour the achievement ladder; locked badges keep their shape and drop the colour. `mark-cream`, `mark-nori`, `mark-ink` and `mark-blush` belong to the brand mark only. The raw Mantine palettes (`indigo-0` to `indigo-9`, `rose-*`, `emerald-*`, `amber-*`, `dark-0` to `dark-9`) exist for Mantine `color` props that need a shade; reach for the semantic tokens first.

**Contrast rules.** Text on a fill takes that fill's own foreground: `primary-on` on `primary` (Mantine autoContrast picks black on Emerald and Amber), `ok-on` on `ok`, white on `danger-fill`. Brand text on the surfaces is `brand-fg`; brand text over cover art is `brand-fg-on-art`, because the scrim stays dark in Light.

**Remaining gaps**, kept exact and flagged in each token's note: `tier-1`, `tier-5` and `tier-6` read between 3.7 and 4.4:1 on dark `surface`, and `rating` is 3.3:1 on white. Use them for icons and rims, not small text.

## Type

Two families. **Bricolage Grotesque** (`display`) is for titles only: a series, a page or the product naming itself. **Inter** (`sans`) sets everything that is read or operated. Bricolage has no Cyrillic or CJK, so those titles fall through to Inter by the stack. Inter runs with character variants `cv02 cv03 cv04 cv11` and antialiasing on.

- Display: `display` (series hero, Rewind), `route`, `page-title` (every `PageHeader`), `section-title` (every `SectionHeader`), `wordmark`. All at 700 with -0.025em tracking (-0.02em for section titles).
- Headings: `h1` to `h4`, Mantine's Title scale (1.9rem 800, 1.5rem 800, 1.2rem 700, 1rem 700).
- Roles: pick the tier by what the text is, not how big you want it. `body` (0.875rem) is the default. `meta` for chips and status words, `micro` for counts and table headers, `label` for uppercase labels over figures, `badge-text` for the smallest badges. `figure` and `stat-value` for numbers.
- The same scale is exposed as CSS variables (`type-display` through `type-badge`); the three largest are fluid `clamp()` values. If no tier fits, the scale gains a tier; call sites never get an off-scale size.
- Weights come from six steps: `fw-regular` 400, `fw-medium` 500, `fw-semibold` 600, `fw-strong` 650, `fw-bold` 700, `fw-heavy` 800. A weight between two steps belongs on one of them.
- Any count, ratio or date shown next to another uses tabular figures (the `.tnum` class).
- `mono` for naming-format tokens, paths and logs.

## Space and layout

- Spacing is Mantine's default scale: `spacing-xs` 10px, `spacing-sm` 12px, `spacing-md` 16px, `spacing-lg` 20px, `spacing-xl` 32px. Panels and modals pad at `spacing-lg`; stat tiles and operational pages at `spacing-md`; a `SectionHeader` sits `spacing-xl` below the previous section and `spacing-sm` above its content.
- Standard pages cap at `content-wide` (72rem); reading text at `content-prose` (42rem). Editorial sections breathe by `section-space`.
- Three page styles: standard, editorial (more air), operational (denser: tables at 40px rows, panels at `spacing-md`).
- The hero band (cover art bleeding behind glass) is reserved for the series page, the Discover modal, sign-in and Rewind. Never hang a panel or a band beside a `PageHeader`.

## Shape, borders and depth

- Radii, smallest to largest: `radius-2xs` 2px (tiny marks), `radius-xs` 4px (small thumbnails, keys), `radius-sm`/`radius-control` 6px (badges, checkboxes), `radius-chip` 7px (tag chips, squarer than a pill), `radius-thumb` 8px (row thumbnails), `radius-md`/`radius-surface` 9px (buttons, inputs, strips), `radius-hero` 12px (hero posters, glass panels, the brand-mark tile), `radius-lg`/`radius-overlay` 13px (cards, panels, covers, modals, overlays), `radius-xl` 20px (large hero surfaces).
- Anything with round ends, whatever its height, takes `radius-pill`: progress bars and their fills, cover badges, seeds. Never set a radius of half the height by hand.
- No other radius. A value between two steps belongs on one of them.
- Borders, not shadows. A `Panel` has a border and no shadow; when it needs emphasis it gets a 2px accent edge (`brand`, a status token, or `border-strong`), not a drop shadow.
- Shadows only where they read: `shadow-lg` under a hovered cover card, `shadow-raised` for the light theme's floating layer (none in dark, where a shadow on near-black is invisible).
- Modals: centred, `radius-lg`, `spacing-lg` padding, a 3px blur over a 55% scrim, a sectioned header over a hairline with only the body scrolling.

## Motion

{{MOTION}}
- Cover cards lift 4px on hover and the art scales to 1.05. A `StatusDot` pulses for work in flight.
- Everything that moves stops under `prefers-reduced-motion`.

## Focus and states

- Focus: a solid 2px `brand` outline at 2px offset.
- Selected: `brand` border plus a soft `brand-glow` halo (cover cards), or brand at 10% behind a table row.
- Active chip: `brand` border, brand at 35% fill, `ink-hi` text.
- Loading: skeletons in place of the content, keeping labels and icons in place and blanking only the numbers, so a count never reads as zero before it arrives. No page-level spinners.
- Text selection highlights in `brand-glow`.

## Iconography

- Icons are **Tabler Icons** (`@tabler/icons-react`), outline style. 20px at stroke 1.8 to 1.9 in headers and tiles; 16px in buttons; 11 to 12px inside cover badges.
- Icons inherit `currentColor`: an icon in a `SectionHeader` is `brand-fg`, in a `StatTile` the tile's accent, in a badge white. The SVGs in the Icons group are the raw Tabler files, drawn in `currentColor`, so they show black where they are displayed as images.
- A status always pairs its colour with an icon or a word (the status helpers return both).
- No emoji in the interface.

## Logo

The mark is a maki roll with a face: a cream body (`mark-cream`), nori bands top and bottom (`mark-nori`), outlined and featured in `mark-ink`, with `mark-blush` cheeks. In the navbar it sits in a 44px `brand-mark` tile (brand at 16% over `surface-2`, a brand-tinted border, `radius-hero`) beside the wordmark "Maki" set in `wordmark` and an uppercase `label` line, "Manga manager", in `ink-3`. The mark's colours are fixed; never recolour it to the accent.

## Not synced

Built from `OrbitMPGH/Maki` at {{REF}} (`frontend/src/theme.css`, `theme.ts`, `theme-context.tsx`, `components/ui/`). Left out: the per-feature rules in `theme.css` (reader, series hero, charts, and Discover beyond its cards); the `--ease` curve and `--dur-*` durations (motion has no token family here, so they live in the Motion section) and `--reader-overlay-z-index`; only the Latin subsets of both fonts. Components are static renditions styled by `components/bundle.css` rather than a built bundle. Every component in `components/ui/` has a card; `RecommendationCard` and `RecommendationRow` are covered by the DiscoverRail and SeriesRow cards, and the `.ts` helpers beside them (status, viewPrefs, useWindowedRows and the rest) are hooks and data, so they have none.
