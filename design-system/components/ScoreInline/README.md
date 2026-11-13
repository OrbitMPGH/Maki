# ScoreInline

A star and a number, set inline in a meta line, a row or a table cell. It is the score for places that are not on cover art; on a cover use the cover's own rating badge.

`ScoreInline` takes the API's community score (0 to 100) and prints it out of ten with one decimal ("8.4"). `YourScoreInline` takes the reader's own 1 to 10 rating and prints the integer with the star in the accent colour, so a glance tells the two apart: gold star for the community, `brand` star for you. The number is `ink-hi` at `fw-strong` with tabular figures, so a column of scores lines up. A provisional score drops to `ink-3`.

Two sizes: `sm` (12px star, the default) for dense rows and `md` (14px) for a row that has room.

**The consumer provides:** `score` or `rating`, and optionally `size`. Both forward `className` and span props.
