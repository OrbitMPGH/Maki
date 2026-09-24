# TableSkeleton

A ruled table's outline while its rows load: the same `Panel`, `panel-table` and row height as the real table, so nothing moves when the data lands.

Header cells get an 8px bar (64px wide in the first column, 44px after); body cells get 10px bars at widths that cycle through 72, 48, 60, 38, 54 and 66% so the rows do not look stamped. Bars are Mantine `Skeleton`s: `dark-4` in dark themes, gray-3 in Light, pulsing, still under reduced motion. The panel is `aria-hidden`.

**The consumer provides:** `columns` (match the real table) and `rows` (default 6).

Use it in place of a spinner for any `panel-table`. Other loading states follow the same rule: keep labels, icons and layout in place and blank only the values (`StatTile` and `FigureStrip` take `loading` for this).
