# Bundled fonts

Self-hosted rather than pulled from Google Fonts at runtime. Maki is often run on a LAN with no
outbound access, where a CDN link means no fonts at all, and on a public instance it would leak
every reader's IP to a third party on each page load. The `@font-face` declarations live at the top
of `frontend/src/theme.css`.

Both faces are licensed under the SIL Open Font License 1.1; `OFL.txt` is the Anton copy and the
same licence covers Inter.

| File | Family | Source |
| --- | --- | --- |
| `inter-latin.woff2`, `inter-latin-ext.woff2` | Inter, variable 400-700 | https://fonts.google.com/specimen/Inter |
| `anton-latin.woff2`, `anton-latin-ext.woff2` | Anton, 400 | https://fonts.google.com/specimen/Anton |

Latin and latin-ext subsets only. Neither face has CJK coverage, so Japanese and Korean titles
resolve through the fallback stack in `theme.ts`, which is deliberate: bundling a CJK face would add
several megabytes for text that the system already renders well.

To refresh a file, request the CSS with a browser user agent (`curl -A "Mozilla/5.0 ..."`,
otherwise Google serves a legacy format), then download the `latin` and `latin-ext` `src` URLs it
names. The `unicode-range` values in `theme.css` come from that same CSS and must be updated
alongside the files.
