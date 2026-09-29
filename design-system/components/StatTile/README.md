# StatTile

A compact metric tile: uppercase label, a big tabular number in the display face, and an optional delta. No icon and no coloured rule.

The value uses the `figure` face (26px) with tabular figures so a row of tiles lines up. `icon` and `accent` are still accepted so older call sites compile, but the tile no longer draws them.

**The consumer provides:** `label`, `value`, optional `hint` (a dimmed related count), `delta` (fractional change: 0.12 prints +12%, `null` prints a dash because a change from zero is not a percentage), `deltaLabel`, `invertDelta` for metrics where down is good, and `loading` (keeps the label, blanks only the number).

Used on Stats with deltas; achievements show standing totals without one.
