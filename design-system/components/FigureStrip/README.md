# FigureStrip

A row of labelled counts in one ruled strip: the operational counterpart of the hero figures.

Numbers use `figure` (1.3125rem, display face, 700, tabular) in `ink-hi`; labels are `label` style uppercase in `ink-4`. A figure's `tone` (`ok`, `warn`, `danger`, `info`) colours it only while non-zero: an empty "Failed" is good news and drops to `ink-4`.

**Panel variant.** `variant="panel"` is a page's headline row: a bordered `surface` card with `shadow-card`, cells padded 14px 20px, figures at 27px in the `figure` face, labels 10.5px uppercase in `ink-4`. It takes an optional `middle` node, rendered as its own cell with a 1px `border` on each side and a 228px minimum width, after the first `middleAfter` figures (centred when omitted). Home uses it for the level bar between the library counts and the unread counts. Below 900px the middle cell drops to its own full-width row.

**The consumer provides:** `figures` (`{ label, value, tone? }[]`), `flush` when it already sits inside a Panel, `loading` to keep labels and blank the numbers so a count never reads as zero before it arrives, and for the panel variant `variant`, `middle` and `middleAfter`.
