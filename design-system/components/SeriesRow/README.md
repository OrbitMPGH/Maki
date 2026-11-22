# SeriesRow

The list-view twin of `CoverCard`: one horizontal row per owned series with a thumbnail, title, year, status, overview and download and read progress.

On `surface` with a `border` outline at `radius-lg`, `spacing-sm` padding; the thumbnail is 2:3 at 8px radius. Hover lifts 2px with `border-strong` and `shadow-sm`; selected in bulk mode takes a `brand` border and a 1px `brand` ring, with a round `row-check` top left.

**Density** sets the thumbnail and the overview clamp: `compact` 48px and one line, `default` 56px and two, `comfortable` 72px and three, with the title up a tier to `subhead`.

The header shows the publication status as a light badge, followed by the monitor eye and a bell when muted. The progress row can show in-flight work, a read ring, a neutral “N new” pill and a “Read” pill in the accent when all downloads have been read. The download bar uses `brand`, turns green when complete and ends with the tabular chapter count.

**The consumer provides:** `series`, `selectMode`, `selected`, `readTracking`, `density`, `onToggle(id)`. `RecommendationRow` (Discover's list view) reuses these classes so the grid and list toggle reads as the same feature in both places. Memoized and built from plain elements for the same reason as `CoverCard`.
