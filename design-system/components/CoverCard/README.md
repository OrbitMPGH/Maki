# CoverCard

The poster card for the library grid: cover art is the hero, with a bottom scrim carrying the title, a download-progress bar and a have/total count.

2:3 aspect, `radius-cover`, a 1px `cover-rim` edge and `shadow-cover` on `surface`. The rim is translucent: it disappears over bright art and only shows where the scrimmed foot meets the page, so the card reads as a book on a shelf rather than a bordered tile. On hover it lifts 4px, takes `cover-rim-hover` and `shadow-cover-hover`, and the art scales to 1.05 (no lift under reduced motion). Selected in bulk mode: `brand` border plus a 1px `brand` ring, no glow.

**Corners, one mark each at rest:** top left shows a download in progress (an `info` disc) or, otherwise, the read-progress ring (`info`, `ok` when all read). Queued work is not shown on the cover. Top right always shows the publication status disc in the status colour; a series that is not monitored dims it (`data-unmonitored`) and its tooltip says so. Monitored is the normal case and has no mark.

**Hover-only chips:** the eye-off (not monitored) and the muted bell sit beside the status disc and fade in (`dur-base`, `ease`) on hover or keyboard focus on devices with a real pointer, and are always visible on touch. The status disc, read ring and download disc never hide. Only the small chips animate opacity; the card itself gets no extra layer.

**Bottom:** title in white, `body` size at 650, clamped to two lines; a 4px bar in `brand` (`ok` when complete); the count at `micro` size, tabular. When no source has listed a chapter and nothing is on disk, the bar stays empty and the count says "No chapters listed", so the card keeps the same shape as its neighbours.

**The consumer provides:** a `series`, `selectMode`, `selected`, `readTracking`, `onToggle(id)`. Built from plain elements and CSS instead of Mantine components on purpose: a grid mounts hundreds of these, so keep it cheap (no `backdrop-filter` on badges, no per-card Tooltip).
