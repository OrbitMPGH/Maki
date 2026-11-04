# FigureStrip

Every row of figures in the app: equal cells in one ruled strip, number over label. Library, Health, Activity, the import plan, Stats (Reading and Library) and Discover's Taste tab all use it. There is no separate stat tile.

Numbers use `figure` (1.3125rem, display face, 700, tabular) in `ink-hi`; labels are `meta` size, sentence case, in `ink-3`. A figure's `tone` (`ok`, `warn`, `danger`, `info`) colours it only while non-zero: an empty "Failed" is good news and drops to `ink-4`.

A value can be a count (formatted by the strip) or something the caller already formatted ("14h 20m", "62%", "3 / 1"); pass `zero` with a formatted value that stands for nothing (a dash) so it greys out like a zero count. `delta` adds the change against the previous window as a chip beside the number (`ok` tint when up, neutral otherwise, never red), left off a zero. `hint` is a `micro` line in `ink-3` under the label.

Cells are equal width: the grid auto-fits cells of at least 8.5rem. Call sites with a known count fix the columns so a wrapped strip never leaves one cell alone (`stats-figures`: 6, 3, 2; `taste-figures`: 4, 2).

**The consumer provides:** `figures` (`{ label, value, tone?, zero?, delta?, hint? }[]`), `flush` when it already sits inside a Panel, `loading` to keep labels and blank the numbers so a count never reads as zero before it arrives, `quietWhenZero` to shrink to one line when every count is zero (Activity's queue), and `deltaLabel` for the chips' tooltip.
