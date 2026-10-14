# SectionHeader

The heading above a rail or grid: an optional glyph, the title, an optional count, and a rule running to an optional right-aligned action.

The icon, when passed, is a bare Tabler glyph at 16px, stroke 1.75, in `ink-3`: no disc, no tint. The title uses `section-title` (display face, 1.3rem, 700). The count is plain tabular text in `ink-4` at `type-meta`, with no pill. The rule is a flat 1px `border` line, giving a run of rails a line to read down. A `subtle` Button in the action slot renders in `ink-3`.

**The consumer provides:** optional `icon`, `title`, optional `count` (omit it when the rail already shows how many it holds, as Home does), optional `action` (a `subtle` Button: "Find more", "Refresh"), and `chevron` to draw a trailing chevron after a single text action that opens a bigger view.

Shared by Discover, the series page's related rail and Home.
