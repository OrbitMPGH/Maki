# CoverCard

The poster card for the library grid: cover art is the hero, with a bottom scrim carrying the title, a download-progress bar and a have/total count.

2:3 aspect, `radius-lg`, `border` outline on `surface`. On hover it lifts 4px, takes `border-strong` and `shadow-lg`, and the art scales to 1.05 (no lift under reduced motion). Selected in bulk mode: `brand` border plus a 1px `brand` ring, no glow.

**Corners:** top left carries in-flight work (a badge in the status token, e.g. `info` for queued), a read-progress ring (`info`, `ok` when all read) and an unread count on the `brand` fill in `brand-on`. Top right carries the monitor eye (dimmed when monitored, clear eye-off when not), a muted-bell badge only when muted, and the publication status badge. Badges are 20px tall, `label` size, 700, uppercase, white on the status colour, except the unread count.

**Hover-only chips:** the monitor eye and the muted bell fade in (`dur-base`, `ease`) on hover or keyboard focus on devices with a real pointer, and are always visible on touch. The status badge, read ring, unread count and download badge never hide. Only the two small chips animate opacity; the card itself gets no extra layer.

**Bottom:** title in white, `body` size at 650, clamped to two lines; a 4px bar in `brand` (`ok` when complete); the count at `micro` size, tabular.

**The consumer provides:** a `series`, `selectMode`, `selected`, `readTracking`, `onToggle(id)`. Built from plain elements and CSS instead of Mantine components on purpose: a grid mounts hundreds of these, so keep it cheap (no `backdrop-filter` on badges, no per-card Tooltip).
