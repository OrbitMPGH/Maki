# StatusDot

A status as a 7px coloured dot and a word, for dense tables where a filled badge on every row would stripe the column.

`tone` is a token stem (`ok`, `warn`, `danger`, `info`, `watched`, `neutral`), usually from `statusToken()`. The word is `ink-2` at `meta` size, so the colour never has to carry the meaning alone. `live` pulses the dot for work in flight; the pulse stops under reduced motion.

It is the status cell of the tables: chapter rows, the Activity queue and history, Health files and checks, Scrobble, a series' Files, Tracking and Sources tabs, and the library list row. A row carries one dot; anything else in the cell (a read mark, a page count, a removal date) is plain text beside it, and the read state is a coloured word in `ok` or `watched`. Quality tiers, source and origin chips and counts stay badges, since they carry a hue or a number a dot would lose.

**The consumer provides:** `tone`, the translated word as children, optional `live`. Accepts a ref so it can sit directly inside a Tooltip.
