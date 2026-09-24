# SeriesRow

The list-view twin of `CoverCard`: one horizontal row per owned series with a thumbnail, title, year, status, overview and download and read progress.

On `surface` with a `border` outline at `radius-lg`, `spacing-sm` padding; the thumbnail is 2:3 at 8px radius. Hover lifts 2px with `border-strong` and `shadow-sm`; selected in bulk mode takes a `brand` border and a `brand-glow` halo, with a round `row-check` top left.

**Density** sets the thumbnail and the overview clamp: `compact` 48px and one line, `default` 56px and two, `comfortable` 72px and three, with the title up a tier to `subhead`.

**Badges** are the cover card's, in the same status tokens: publication status and the monitor eye in the header (a muted bell only when muted); in the progress row, in-flight work, a read ring, and either the unread count on `brand` or a plain "Read" on `ok` once none are left. The bar is 4px on `surface-2`, `brand` filling, `ok` when complete; the have/total count is `micro`, tabular, `ink-3`.

**The consumer provides:** `series`, `selectMode`, `selected`, `readTracking`, `density`, `onToggle(id)`. `RecommendationRow` (Discover's list view) reuses these classes so the grid and list toggle reads as the same feature in both places. Memoized and built from plain elements for the same reason as `CoverCard`.
