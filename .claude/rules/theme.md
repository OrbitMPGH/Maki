---
paths:
  - "frontend/src/theme.css"
  - "frontend/src/theme.ts"
  - "frontend/src/theme-context.tsx"
  - "frontend/src/components/ui/**"
---

# Theme, surface layering, Mantine overrides

- **Never write a `:not()` chain in a theme.css override.** Each `:not()` contributes its argument's specificity, so `.mantine-Paper-root:not(.a):not(.b):not(.c)` scores (0,5,0) and silently beats every later rule in the file, including ones written specifically to override it. The failure is quiet: one declaration lands and another does not, on the same selector. Put exclusions inside `:where()`, which contributes zero, and the selector stays at (0,2,0). The blanket Card/Paper rule is written this way for exactly this reason.

- **The blanket Card/Paper rule pins every surface to `--surface` and is load-bearing.** Roughly 104 `<Card>`/`<Paper>` call sites depend on it. Do not delete it to "opt surfaces in"; they would all fall to Mantine defaults at once. Move an individual surface off the base layer with `.layer-raised` / `.layer-sunken` / `.layer-flush` instead.

- **Layer utilities carry a `[data-mantine-color-scheme]` prefix purely to out-specify that blanket rule.** A bare `.layer-raised` loses to it. Keep the prefix and the `.mantine-Paper-root.layer-*` companion selector when adding new ones.

- **The two themes elevate by opposite means, so `raised`/`sunken` are defined per theme, not once.** Dark elevates by lightening; a shadow on near-black is invisible. Light elevates by staying white and casting a real shadow, and recedes by going grey. A single direction does not read in both. Shadow is the light-theme lever, surface lightness is the dark-theme lever.

- **`sunken` is relative to whatever the surface sits on.** In dark, `--surface-sunken` resolves to the canvas, so `.layer-sunken` is invisible on a page background and only does something nested inside a filled surface. Library's index block relies on its rules, not its fill, in dark.

- **`--surface` deliberately sits only 1.06:1 off the canvas.** A resting card reads fine there because it carries a border; do not "fix" it. Only the focal layer needed separating, at 1.25:1. That ceiling is not arbitrary: pushing `--surface-raised` any lighter takes it past `--border` (it already lands 1.05:1 from it), at which point every raised edge disappears. Raised surfaces use `--border-raised`, not `--border`.

- **Do not redefine `--surface-2`.** It has ~32 consumers with genuinely mixed intent: raised overlay chrome in dark, recessed chrome in light (the light navbar). The intent tokens `--surface-raised`/`--surface-sunken` were added beside it rather than on top of it. Migrate call sites deliberately, one at a time.

- **Overlay rules must be theme-agnostic.** Use `[data-mantine-color-scheme]` with no value. The Modal/Menu/Popover rules were previously `='dark'` only, so light overlays fell through the blanket rule and rendered the same colour as the cards behind them. `--shadow-overlay` likewise needs a light definition; dark's is near-opaque black and smudges on the cream canvas. `.mantine-Drawer-content` is a Paper too and is easy to forget when adding an overlay class to the list.

- **One card-shadow token only.** `--shadow-card` existed in both themes with zero consumers in the entire tree and was removed. Two competing shadow tokens is how the first one went dead. `--shadow-raised` is the one.

- **Never write a raw `font-size` in theme.css. Pick a `--type-*` tier.** The scale replaced 44 ad-hoc values, 13 of which sat between 0.61rem and 0.8rem: at a 16px root those steps are a fraction of a pixel, so they read as noise, not hierarchy. If no tier fits, the scale is missing one; add the tier rather than an off-scale value at the call site. Tiers, largest first: `display`, `route`, `feature`, `section`, `subhead`, `body`, `meta`, `micro`, `label`, `badge`.

- **The hero and rewind display sizes are deliberately off-scale and stay that way.** `.series-hero-title`, the `.discover-hero` overrides and every `.rewind-*` size are the authored display language the redesign is meant to keep. `--type-display` matches the series hero exactly so the tier has a real consumer; do not "consolidate" the others into it.

- **A token with no consumer is a dead token.** `--shadow-card` shipped in both themes and was never referenced anywhere in the tree. When adding one, wire it at the same time.

- **Never hand-roll an art backdrop. Render `<HeroBackdrop coverUrl={...} />`.** It emits the four layers (`.series-hero-art`, `-falloff`, `-scrim-x`, `-scrim-y`) and is already shared by the series page, the Discover hero, the Discover detail modal and Home's Continue reading tiles. Those gradients are tuned as one recipe; theme.css says so explicitly, and it is what stops the surfaces drifting the first time one gradient is touched. A local copy of the layers is the drift.

- **Why the recipe is shaped the way it is**, when you need to override it for a new surface: the art is the cover at `background-size` larger than the box, blurred and scaled up, because filling a 2:3 poster to a much wider band is always a crop and the blur is what stops the crop reading as a mistake. The falloff keeps the light in one corner instead of dimming the band evenly. The horizontal scrim buys the text its contrast on one side only. The vertical scrim dissolves a full-bleed band into the page and is unnecessary on a bordered card. A single flat overlay over art reads as a smudge. Override only the geometry (`background-size`, `background-position`) for a smaller surface; leave the gradients alone.

- **A surface hosting the backdrop needs `isolation: isolate` and `overflow: hidden`**, with the content given a `z-index` above the layers. Without the isolation the absolutely positioned layers escape the surface's rounded corners.

- **Never name a Mantine palette colour in theme.css.** `--mantine-color-dark-4` resolves to `#3a3530` in *both* themes, so a rule using it draws a dark line on the light canvas. Two rules did; one was dead and one was visible on the series page. Use `--border`, `--border-strong` or `--line-soft`.

- **Flush for a table is `--table-striped-color: transparent`, not `.layer-flush`.** That utility only reaches Paper and Card backgrounds; Mantine paints table stripes from its own variable. `.panel-table`, the shared dense-table class, is flushed this way so operational rows read as a table rather than a stack of slabs. When you remove a resting fill like that, check what feedback the row has left: the dark row hover was `rgba(255,255,255,0.02)` and became the only cue, so it was strengthened at the same time.

- **A `.panel-table` rule can be dead without looking it.** `.mantine-Table-tbody .mantine-Table-td` scores (0,2,0) and beats `.panel-table tbody td` at (0,1,2), so cell borders set through the semantic class silently lose to the Mantine one.

- **There is already a global `prefers-reduced-motion` rule.** It collapses `animation-duration` and `transition-duration` to 0.001ms on `*` with `!important`. So a per-component reduced-motion block does NOT need `transition: none`; that is already handled. What the global rule cannot do is cancel a *transform* (a hover scale still applies, just instantly) or an `animation-delay` (an animation reduced to 0.001ms still waits out its delay before appearing). Cancel those two, nothing else.

- **`--ease` and the mount reveal are shared decisions.** A featured cover fading and scaling in on mount is `EditorialMotion` (`mode="image-scale"`), used by the series hero, the Discover detail modal and Home's Continue reading tiles. Add callers rather than a second animation that means the same thing; stagger a row of them with `nth-child` in CSS so the shared component stays prop-free.

Applies to page-level surface work too, not only the files in `paths`: reach for the layer utilities rather than a new one-off `background:` on a page class.
