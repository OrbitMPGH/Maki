# SectionHeader

The heading above a rail or grid: an accent icon, the title, an optional count, and a rule running to an optional right-aligned action.

The icon is a Tabler icon at 21px, stroke 1.9, in `brand-fg`. The title uses `section-title` (display face, `type-section-title`, 700), one step under the page title. The count is a figure: display face, 700, tabular, in `ink-3` at `type-subhead`, with no pill. Inside the Continue card's Next up tray the header shrinks to a label and the count goes back to plain `ink-4` text at `type-meta`. The rule is a flat 1px `border` line, giving a run of rails a line to read down. A `subtle` Button in the action slot renders in `ink-3`.

**The consumer provides:** `icon`, `title`, optional `count` (omit it when the rail already shows how many it holds, as Home does), optional `action` (a `subtle` Button: "Find more", "Refresh"), and `chevron` to draw a trailing chevron after a single text action that opens a bigger view.

Shared by Discover, the series page's related rail and Home.
