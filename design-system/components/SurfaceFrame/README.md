# SurfaceFrame

The outer wrapper of every route: sets the page's width and its page style, and through the style its density and vertical rhythm.

**Width:** `wide` (default) caps at `content-wide` (72rem) and centres; `full` spans the main column.

**Page style** retunes `section-space` and the density of what sits inside:
- `standard`: `clamp(1.75rem, 3vw, 3.5rem)` between sections.
- `editorial`: `clamp(2.5rem, 5vw, 5.5rem)`. For pages you browse: Home, Library, Discover, Stats, a series, a creator. These pair with `width="full"`.
- `operational`: `clamp(1.5rem, 2.5vw, 2.75rem)`. For pages you operate: Activity, Requests, Import, Health, Notifications, Scrobble, Settings. Panels drop to `spacing-md` padding, panel tables to 40px rows at `meta` size with `ink-3` headers, and the `PageHeader` goes `compact`.

**Tables inside panels** use the `panel-table` class on a Mantine `Table` in a zero-padding `Panel` (`table-panel`): `micro` 600 headers over a `border` rule, 56px rows split by `hairline` (40px when operational), no rule under the last row, a selected row at brand 10%. Show row status with `StatusDot`, not badges.

**The consumer provides:** `children`, `width`, `pageStyle`, and any div prop (pages add a `className` for their own layout).
