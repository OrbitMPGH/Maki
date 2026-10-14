Maki is a self-hosted manga collection manager: add a series once and it keeps it complete. The interface is content-first and cinematic. By default the ground is a near-black tinted toward the accent, the ink is a cool off-white, and the accent is indigo. The chrome stays quiet so the covers carry the colour.

## Voice and copy

- Write plain, direct sentences, the way a developer explains something to a teammate. Say what happens: "Add series", "Refresh the catalogue", "Browse the catalogue", never "Submit" or "OK".
- Sentence case everywhere: buttons, titles, menu items, labels and badges. The sidebar group labels are the one uppercase line in the app.
- Address the reader as "you" and the product as "Maki": "Maki finds its chapters and keeps it complete." No "we".
- Empty states are statements, not apologies: "No series yet", "No chapters read in this period.", "Queue is empty". Follow with one line on what would fill it and at most one action.
- Explain a consequence in a short clause after a colon: "Sliding: a failed sign-in resets the timer." "No change: this title was already in that state."
- No em dashes. No emoji in UI copy. No filler words ("seamless", "robust", "leverage") and no "it's not just X, it's Y".
- Every string is translated (Maki ships in eleven languages). A status Maki has no word for is shown exactly as the server sent it rather than guessed at.

## Colour

**One accent, near-black ground.** `brand` is the only accent. It marks progress, selection, focus and the one primary action. Everything else is surfaces and neutral ink. Covers bring their own colour; the chrome stays out of their way.

**Appearance.** The app's picker makes two choices per user. A background: `Tinted black` (the default), `Night`, `Charcoal`, `Moss`, `Light` or `Match system`. An accent: `Indigo` (the default), `Rose`, `Blush`, `Emerald` or `Amber`. Tinted black mixes every surface 4% from neutral black toward the active accent, so the ground belongs to whichever accent is on. Night is blue-black, Charcoal a neutral grey with warmer ink, Moss a faint green.

**Themes here.** Six: the dark theme (`Indigo`), four dark accent swaps (`Rose`, `Blush`, `Emerald`, `Amber`) and `Light` (Indigo on cool paper, white cards). All the dark themes sit on Night, the reference ramp in `:root`: Tinted black is computed from the accent in the browser, so its exact values cannot be listed as tokens. Treat Night's values as the shape of the ladder and expect the default ground to read a touch more neutral and accent-warmed. The accent themes change only the brand group (`brand`, `brand-hover`, `brand-on`, `brand-fg`, `brand-fg-on-art`, `brand-glow`, `primary`, `primary-hover`, `primary-on`); surfaces, ink and status stay those of dark. Blush, Emerald and Amber are pale enough that their fills take a near-black label. Light deepens `brand-fg` per accent so brand text holds on white.

**The layer ladder.** Build every screen from these, in order of height:
- `app-bg`: the page ground, flat. No washes, glows or grain; Tinted black already mixes 4% of the accent into every surface, which is all the temperature the ground needs. The app header and the sign-in frame sit on it opaque.
- `surface`: the default layer. Every Card, Paper and `Panel` sits here.
- `surface-2`: one step up. Tag chips, inputs at rest.
- `surface-raised`: overlays (modals, drawers, menus, popovers). Dark elevates by lightening (it aliases `surface-2`); light elevates by staying white and casting `shadow-raised`. Never assume one direction reads in both.
- `surface-sunken`: recessed wells. `app-bg` in dark, `#eceef6` in light.
- `surface-hover` for hover fills on rows and chips.

Borders are 1px `border`; hovered or emphasised edges take `border-strong`; row dividers inside tables use `hairline`.

**Ink.** Primary, secondary and tertiary, each 4.5:1 or better on `surface-hover` on every ground. `ink-hi` for titles, figures and the active item and `ink` for body copy (Mantine's text colour maps here); `ink-3` is secondary, for labels, descriptions and meta lines (Mantine's dimmed colour maps here); `ink-4` is tertiary, for timestamps and zero counts. `ink-2` is an alias of `ink-3`.

**Status.** Semantic hues live in tokens, never in Mantine's stock palette, so "Completed" is the same green on a cover, a badge and a table row and follows the light theme: `ok`, `warn`, `info`, `danger`, `watched` (read state, kept apart from brand so a read row never looks selected), `suggestive` (olive, for the Suggestive rating and the 65 to 79 score band), `neutral` (unknown or idle). Each has a `-soft` partner at 15% for a tinted fill behind it. Every status fill carries its own foreground, near-black in dark and white in Light: `ok-on`, `warn-on`, `info-on`, `danger-on`, `watched-on` and `neutral-on`. Filled danger buttons are the exception: they sit on `danger-fill`, a deeper red, so their white labels hold 5.6:1. Status is always carried by a word or an icon as well as the hue.

Content ratings run green to red on purpose: Safe `ok`, Suggestive `suggestive`, Erotica `warn`, Pornographic `danger`. Catalogue scores band the same way: 80 and up `ok`, 65 to 79 `suggestive`, 50 to 64 `warn`, below 50 `danger`.

**Other palettes.** `rating` is the star colour. `tier-1` to `tier-6` (with `-soft` fills) colour the achievement ladder; locked badges keep their shape and drop the colour. `mark-cream`, `mark-nori`, `mark-ink` and `mark-blush` belong to the brand mark only. The raw Mantine palettes (`indigo-0` to `indigo-9`, `rose-*`, `emerald-*`, `amber-*`, `dark-0` to `dark-9`) exist for Mantine `color` props that need a shade; reach for the semantic tokens first.

**Contrast rules.** Text on a fill takes that fill's own foreground: `primary-on` on `primary` (Mantine autoContrast picks black on Emerald and Amber), the matching `-on` token on each status fill (`ok-on` on `ok`), white on `danger-fill`. Brand text on the surfaces is `brand-fg`; brand text over cover art is `brand-fg-on-art`, because the scrim stays dark in Light.

**Remaining gaps**, kept exact and flagged in each token's note: `tier-1`, `tier-5` and `tier-6` read between 3.7 and 4.4:1 on dark `surface`, and `rating` is 3.3:1 on white. Use them for icons and rims, not small text.

## Type

Two families. **Bricolage Grotesque** (`display`) is for names and figures: page, section and modal titles, every series title (hero, cover cards, reading cards, list rows) and the headline numbers. **Inter** (`sans`) sets body copy, tables, inputs and buttons. Bricolage has no Cyrillic or CJK, so those titles fall through to Inter by the stack. Inter runs with character variants `cv02 cv03 cv04 cv11` and antialiasing on.

- Display: `display` (series hero, Rewind), `route`, `page-title` (every `PageHeader`), `section-title` (every `SectionHeader`), `wordmark`. All at 700 with -0.025em tracking (-0.02em for section titles).
- Headings: `h1` to `h4`, Mantine's Title scale (1.9rem 800, 1.5rem 800, 1.2rem 700, 1rem 700).
- Roles: pick the tier by what the text is, not how big you want it. `body` (0.9375rem, Mantine `sm`) is the default. `meta` (0.8125rem, Mantine `xs`) for labels, cover titles, chips and status words, `micro` (0.75rem) is the floor for anything read, `label` for sentence-case labels over figures, `badge-text` for badges only. `figure` and `stat-value` for numbers.
- Figures (strips, stat tiles, the hero figures and the level) set in the display face through the `.figure` class: 700, -0.02em, tabular. Sizes stay per call site. Their labels are sentence case at `meta` size in `ink-3`.
- The same scale is exposed as CSS variables (`type-display` through `type-badge`); the three largest are fluid `clamp()` values. If no tier fits, the scale gains a tier; call sites never get an off-scale size.
- Weights come from six steps: `fw-regular` 400, `fw-medium` 500, `fw-semibold` 600, `fw-strong` 650, `fw-bold` 700, `fw-heavy` 800. A weight between two steps belongs on one of them.
- Any count, ratio or date shown next to another uses tabular figures (the `.tnum` class).
- `mono` for naming-format tokens, paths and logs.

## Space and layout

- Spacing is Mantine's default scale: `spacing-xs` 10px, `spacing-sm` 12px, `spacing-md` 16px, `spacing-lg` 20px, `spacing-xl` 32px. Panels and modals pad at `spacing-lg`; stat tiles and operational pages at `spacing-md`; a `SectionHeader` sits `spacing-xl` below the previous section and `spacing-sm` above its content.
- `SectionHeader`: the count is plain tabular text in `ink-4` (no pill), the rule is a flat 1px `border`, and the action is a `subtle` button in `ink-3` that can end in a chevron.
- `FigureStrip` has a `panel` variant for a page's headline row: a shadowed `surface` card with 27px figures, and an optional middle cell between them (Home puts the level there). `StatTile` has no coloured rail and no icon; the number carries it.
- `EmptyState` takes a `mood` for the inline form: the mascot at 56px beside the text (asleep for an empty queue or list, pleased when nothing is left to deal with, asking when it needs something from you). Page-level `art` states are unchanged.
- `CoverCard` shows its monitor eye and mute bell only on hover or focus where there is a real pointer, and always on touch. A library with fewer than 12 series ends its grid in a dashed add cell.
- Settings sections put the title in the display face and its explanation above the panel; the panel holds only the fields, and the content column is no longer capped.
- Standard pages cap at `content-wide` (72rem); reading text at `content-prose` (42rem). Editorial sections breathe by `section-space`.
- Three page styles: standard, editorial (more air), operational (denser: tables at 40px rows, panels at `spacing-md`).
- The hero band (cover art bleeding behind the page title) is reserved for the series page, the Discover modal and Rewind. Panels over it are opaque `surface-2` with a `border` hairline, never glass. Never hang a panel or a band beside a `PageHeader`.

## Shape, borders and depth

- Radii: three values plus pill. `radius-control` 6px (Mantine `xs` and `sm`; badges, checkboxes, chips, row thumbnails), `radius-surface` 10px (Mantine `md`; buttons, inputs, cards, panels, strips, cover cards), `radius-overlay` 13px (Mantine `lg`; modals, menus, popovers, hero posters and panels), `radius-pill` 999px (Mantine `xl`; bars, round avatars and icon buttons). `radius-2xs`, `radius-chip` and `radius-thumb` alias control, `radius-card` aliases surface and `radius-hero` aliases overlay.
- Anything with round ends, whatever its height, takes `radius-pill`: progress bars and their fills, cover badges, seeds. Never set a radius of half the height by hand.
- No other radius. A value between two steps belongs on one of them.
- Panels carry a border and the 1px `shadow-card` in both schemes, and no accent edges. A panel that needs to report state does it with a `StatusDot` beside its title. No glows and no `backdrop-filter` anywhere: a selected or active thing takes a flat ring or fill, never a blurred halo, and anything floating over content is opaque.
- Shadows only where they read: `shadow-raised` for the light theme's floating layer (none in dark, where a shadow on near-black is invisible).
- Modals: centred, `radius-lg`, `spacing-lg` padding, a 60% scrim with no blur, a sectioned header over a hairline with only the body scrolling.

## Motion

{{MOTION}}
- Cover cards stay put on hover: the border steps to `border-strong` and the art scales to 1.05. A `StatusDot` pulses for work in flight.
- Everything that moves stops under `prefers-reduced-motion`.

## Focus and states

- Focus: a solid 2px `brand` outline at 2px offset.
- Selected: `brand` border plus a 1px `brand` ring (cover cards, series rows), or brand at 10% behind a table row. A brand ring, not a glow.
- Nav active: a flat fill of brand at 14% with a 2px `brand` bar at the left edge.
- Active chip: `brand` border, brand at 35% fill, `ink-hi` text.
- Loading: skeletons in place of the content, keeping labels and icons in place and blanking only the numbers, so a count never reads as zero before it arrives. No page-level spinners.
- Text selection highlights in `brand-glow`.
- Dirty: a Settings section with unsaved edits shows Save and Discard beside an "Unsaved" marker in its heading row; both disappear once it matches what is saved.

## Iconography

- Icons are **Tabler Icons** (`@tabler/icons-react`), outline style. 20px at stroke 1.8 to 1.9 in headers and tiles; 16px in buttons; 11 to 12px inside cover badges.
- Icons inherit `currentColor`: an icon in a `SectionHeader` is `brand-fg`, in a badge white. The SVGs in the Icons group are the raw Tabler files, drawn in `currentColor`, so they show black where they are displayed as images.
- A status always pairs its colour with an icon or a word (the status helpers return both).
- No emoji in the interface.

## Logo

The mark is a maki roll with a face: a cream body (`mark-cream`), nori bands top and bottom (`mark-nori`), outlined and featured in `mark-ink`, with `mark-blush` cheeks. In the navbar it sits in a 44px `brand-mark` tile (brand at 16% over `surface-2`, a brand-tinted border, `radius-hero`) beside the wordmark "Maki" set in `wordmark` and a `label` line, "Manga manager", in `ink-3`. The mark has four moods that change only the eyes and mouth: awake (the default, and what the navbar uses), asleep (closed eyes and two small z's in `ink-3`), asking (a raised brow and a small o mouth) and pleased (happy eyes and a wide smile). The mark's colours are fixed in every mood; never recolour it to the accent.

## Not synced

Built from `OrbitMPGH/Maki` at {{REF}} (`frontend/src/theme.css`, `theme.ts`, `theme-context.tsx`, `components/ui/`). Left out: the per-feature rules in `theme.css` (reader, series hero, charts, and Discover beyond its cards); the `--ease` curve and `--dur-*` durations (motion has no token family here, so they live in the Motion section) and `--reader-overlay-z-index`; only the Latin subsets of both fonts. Components are static renditions styled by `components/bundle.css` rather than a built bundle. Every component in `components/ui/` has a card; `RecommendationCard` and `RecommendationRow` are covered by the DiscoverRail and SeriesRow cards, and the `.ts` helpers beside them (status, viewPrefs, useWindowedRows and the rest) are hooks and data, so they have none.
