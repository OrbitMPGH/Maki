# Icons

A representative set of the Phosphor icons Maki uses most: trash, plus, warning, eye, eye-slash, arrow-clockwise, check, download-simple, compass, clock, caret-right, fire, book-open, magnifying-glass, play, star, file-zip, gear-six, squares-four, books, house, tray. The app draws them from `frontend/src/lib/icons.tsx`, which `scripts/frontend/generate-icons.mjs` generates from `@phosphor-icons/core`; the full set is at phosphoricons.com.

The files are the raw Phosphor Regular SVGs, filled with `currentColor` on a 256 grid, so shown as images they render black. In the app they inherit the surrounding text colour: `brand-fg` in section headers, the tone colour in stat tiles, white in cover badges. Render them at 16px in buttons, menus and rows (the size Regular is pixel-aligned at), 20 to 21px in headers and tiles, 11 to 13px in badges, where the app switches to Bold. Sparkles are not used anywhere: recommendations take the compass.
