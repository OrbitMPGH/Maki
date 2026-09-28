# FigureStrip

A row of labelled counts in one ruled strip: the operational counterpart of the hero figures.

Numbers use `figure` (1.3125rem, 700, tabular) in `ink-hi`; labels are `label` style uppercase in `ink-4`. A figure's `tone` (`ok`, `warn`, `danger`, `info`) colours it only while non-zero: an empty "Failed" is good news and drops to `ink-4`.

**The consumer provides:** `figures` (`{ label, value, tone? }[]`), `flush` when it already sits inside a Panel, `loading` to keep labels and blank the numbers so a count never reads as zero before it arrives.
