# DiscoverRail

A horizontal rail of catalogue poster cards (`DiscoverRailRow` of `RecommendationCard`s), for MangaBaka items the reader may not own yet.

Cards are 150px wide (`--rail-item-w`), scroll-snapped, `spacing-md` apart, built on the `CoverCard` shell: 2:3 poster, bottom scrim with the title and a sub-line (year, status, chapter count in `micro`). A black rating pill sits top left with a star in `rating`, owned or not; an owned card adds its check top right.

The 24px corner cue shows an accent check for an owned title, a bookmark for a shortlisted title, or an accent plus on hover or focus for an unowned title. The plus stays visible on touch. The poster opens the details. Eligible unowned cards have a separate shortlist button on hover or keyboard focus, and touch users shortlist from the details.

The footer puts a status disc and word before the year, with the chapter count at the other end. The year gives way first on narrow posters. The star and score sit on the artwork.

**Reason line:** hidden by default. A catalogue rail is a row of covers you skim, and the rail's `SectionHeader` already says why they are here. Pass `showReason` only where each pick needs defending ("More like this"); the line sits above the title in `brand-fg-on-art`, `label` size.

**The consumer provides:** `items`, `seriesIdFor(item)` (library id or null), `onOpen(item)`, optional `showReason`. For owned series with download and read progress use `CoverCard`; for rows the recommender produced use `EngineCard`. `RecommendationRow` is the list-view twin and reuses the `SeriesRow` classes.

`Rail` fades whichever edges have more cards past them. When it can find an adjacent `SectionHeader`, it adds scroll arrows to the header and disables the arrow at the reached end. `RailSkeleton` keeps the same poster geometry while the rail loads, including the wider engine variant and an optional heading placeholder.

Built from plain elements, not Mantine: Discover mounts 240 of these at once, and tooltips come from `TipLayer` via `data-tip`.
