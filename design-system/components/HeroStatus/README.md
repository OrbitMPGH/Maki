# HeroStatus

`HeroStatusPills`: the publication status and content rating on the series band, the Discover hero and the Discover modal, so a result and the series it becomes read as one object.

Both are hairline chips, 28px tall at `radius-control`: `border-strong` outline over `app-bg` at 45% (which keeps the edge legible where the backdrop art runs light), `meta` size at 600. The status chip leads with a 7px dot in its status token (`info` for Ongoing, `ok` Completed, `warn` Hiatus, `danger` Cancelled) and `ink` text. The content rating is the quiet one: no dot and `ink-3` text, with a "Content rating" tooltip. The series band adds the reader's own star rating in the same chip, stars in `ink`. One hue on the row, the status dot.

**The consumer provides:** the `status` and `contentRating` wire values, and `tooltipZIndex` inside a modal.
