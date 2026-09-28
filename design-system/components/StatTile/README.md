# StatTile

A compact metric tile: uppercase label, a big tabular number, an icon top right, and a 3px accent rule on the left in the tile's tone.

`accent` picks the tone: `brand` (default), `ok`, `warn`, `info`, `danger`, `gray` (`neutral`). The value uses `stat-value` (26px, 750) with tabular figures so a row of tiles lines up.

**The consumer provides:** `label`, `value`, `icon`, optional `accent`, `hint` (a dimmed related count), `delta` (fractional change: 0.12 prints +12%, `null` prints a dash because a change from zero is not a percentage), `deltaLabel`, `invertDelta` for metrics where down is good, and `loading` (keeps label and icon, blanks only the number).

Used on Stats with deltas; Home and achievements show standing totals without one.
