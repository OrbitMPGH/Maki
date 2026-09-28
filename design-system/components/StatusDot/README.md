# StatusDot

A status as a 7px coloured dot and a word, for dense tables where a filled badge on every row would stripe the column.

`tone` is a token stem (`ok`, `warn`, `danger`, `info`, `watched`, `neutral`), usually from `statusToken()`. The word is `ink-2` at `meta` size, so the colour never has to carry the meaning alone. `live` pulses the dot for work in flight; the pulse stops under reduced motion.

**The consumer provides:** `tone`, the translated word as children, optional `live`. Accepts a ref so it can sit directly inside a Tooltip.
