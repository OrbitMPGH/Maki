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

Applies to page-level surface work too, not only the files in `paths`: reach for the layer utilities rather than a new one-off `background:` on a page class.
