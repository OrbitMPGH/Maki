# StatusDot

A status as a 7px coloured dot and a word, for dense tables where a filled badge on every row would stripe the column.

`tone` is a token stem (`ok`, `warn`, `danger`, `info`, `watched`, `neutral`), usually from `statusToken()`. The word is `ink-2` at `meta` size, so the colour never has to carry the meaning alone. `live` pulses the dot for work in flight; the pulse stops under reduced motion.

It is the status cell of tables where a cell holds one status: the Activity queue and history, Health files and checks, Scrobble, and a series' Files, Tracking and Sources tabs. Chapter rows and the library list row keep tinted badges, because their cell holds two statuses (file and read state, or series and download state) and two dots side by side read as one phrase. Quality tiers, source and origin chips and counts stay badges too, since they carry a hue or a number a dot would lose.

**The consumer provides:** `tone`, the translated word as children, optional `live`. Accepts a ref so it can sit directly inside a Tooltip.
