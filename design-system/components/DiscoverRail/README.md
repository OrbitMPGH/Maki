# DiscoverRail

A horizontal rail of catalogue poster cards (`DiscoverRailRow` of `RecommendationCard`s), for MangaBaka items the reader may not own yet.

Cards are 150px wide (`--rail-item-w`), scroll-snapped, `spacing-md` apart, built on the `CoverCard` shell: 2:3 poster, bottom scrim with the title and a sub-line (year, status, chapter count in `micro`). A black rating pill sits top left with a star in `rating`.

**The corner says what a tap does.** Owned: a persistent 44px `ok` circle with an `ok-on` check ("In library"). Not owned: a `primary` plus in `primary-on` that appears on hover or focus ("View & add"), always visible on touch. The whole poster is one button, so a card is one keyboard stop with a 2px `brand` focus outline around the card.

**Reason line:** hidden by default. A catalogue rail is a row of covers you skim, and the rail's `SectionHeader` already says why they are here. Pass `showReason` only where each pick needs defending ("More like this"); the line sits above the title in `brand-fg-on-art`, `label` size.

**The consumer provides:** `items`, `seriesIdFor(item)` (library id or null), `onOpen(item)`, optional `showReason`. For owned series with download and read progress use `CoverCard`; for rows the recommender produced use `EngineCard`. `RecommendationRow` is the list-view twin and reuses the `SeriesRow` classes.

Built from plain elements, not Mantine: Discover mounts 240 of these at once, and tooltips come from `TipLayer` via `data-tip`.
