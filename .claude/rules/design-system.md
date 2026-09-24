---
paths:
  - "frontend/src/theme.css"
  - "frontend/src/theme.ts"
  - "frontend/src/theme-context.tsx"
  - "frontend/src/components/ui/**"
  - "design-system/**"
  - "scripts/design-system/**"
---

# Design system

The Maki design system (a claude.ai Design System artifact, https://claude.ai/artifact/2qxby6RtK7vtNbgb5y86z9) is built from the code by `scripts/design-system/build.mjs` (`npm run ds:build` in `frontend/`). Output goes to `design-system/out/project/`, which is gitignored.

- **Values are never typed into the design system.** Colours, radii, weights, type sizes, shadows and layout widths come from `theme.css` custom properties (per theme, light and `[data-accent]` overrides applied), and palettes, Mantine radii, shadows, headings and the primary shade from `theme.ts`. Change the code, rebuild.
- **A new custom property in `theme.css` needs a usage note** in `design-system/tokens.notes.json` under `usage`, or the build warns (and exits 1). Removing one leaves a stale note, which also warns. Motion variables (`--ease`, `--dur-*`) have no token family; their notes live under `motion` and the build writes them into the README's Motion section.
- **Colour values the design system can read:** hex, `rgb()`/`rgba()`, `var(--other)` (becomes an alias), and `color-mix(in srgb, var(--x) N%, transparent)` (becomes an exact `rgba`). Any other `color-mix` or expression is skipped with a warning; give such a token a plain value or leave it out of `:root`.
- **`components/bundle.css` is generated:** `design-system/preview.css` (page basics and what Mantine draws at runtime) followed by the theme.css rules for the preview components, with Mantine's runtime variables mapped onto tokens. A preview that needs a new class from theme.css may need its prefix added to `CLASSES` in the build script. Never edit `out/`.
- **Hand-written parts** live in `design-system/`: `README.md` (brand book; `{{REF}}` and `{{MOTION}}` are filled in by the build), `components/<Name>/README.md` and `preview.html`, `components/index.d.ts`, `assets/*/README.md`. A new component in `components/ui/` gets a folder there.
- **Don't publish on every frontend change.** Most commits touch nothing the design system shows. Each build ends by comparing against `design-system/published.json` (the last publish) and lists changed, added and removed tokens and the files that differ; "nothing has changed" means there is nothing to publish. Publish at a release or before handing the design system to someone, not per commit.
- **Publishing:** publish the files the build lists under `design-system/out/project/` to the artifact as its type's `SKILL.md` describes (read `project/design-system.json` first, set its `lastChange`, send it last). Icons and logos are uploads recorded in that index, not files in the repo. Build from a committed tree so the README names a commit rather than "uncommitted changes". Then run the build with `--mark-published` and commit the updated `published.json`.
