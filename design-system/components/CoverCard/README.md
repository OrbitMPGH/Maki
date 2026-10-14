# CoverCard

The poster card for the library grid: cover art is the hero, with a bottom scrim carrying the title, a download-progress bar and a have/total count.

2:3 aspect, `radius-lg`, `border` outline on `surface`. On hover it takes `border-strong` and the art scales to 1.05. Selected in bulk mode: `brand` border plus a 1px `brand` ring, no glow.

**Corners:** top left carries the read-progress ring (`brand`, `ok` when all read) and in-flight work as a 22px dark disc with a download or clock glyph (`info` while downloading). Top right carries three quiet marks, always visible: the monitor eye (dimmer when monitored, brighter eye-off when not), a muted bell only when muted, and the publication status glyph. Each is a 20px dark disc with a 12px glyph in white at 58 to 78%, no colour of its own; the tip names it. The ring and the "N new" pill are the only colour on a cover.

**Bottom:** title in white, `body` size at 650, clamped to two lines; a 4px bar in `neutral` while chapters are missing, `ok` when complete; the have/total count at `micro` size, tabular, and an "N new" pill on `brand` when downloaded chapters are unread.

**The consumer provides:** a `series`, `selectMode`, `selected`, `readTracking`, `onToggle(id)`. Built from plain elements and CSS instead of Mantine components on purpose: a grid mounts hundreds of these, so keep it cheap (no `backdrop-filter` on badges, no per-card Tooltip).
