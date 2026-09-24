# EngineCard

The poster card for rows the recommender produced (`EngineRailRow` of `EngineCard`s): the catalogue card's poster plus a footer that says why this pick is here.

"Trending now" is a ranking and needs no defence; "Based on your recent activity" is a claim about the reader, and a claim you cannot see the grounds for is just a row of covers. So these cards are wider (180px, `--rail-item-engine-w`) and carry the grounds under the poster.

**Footer:** on `surface` over a `hairline`. One reason line in `brand-fg` at `micro` 600, clamped to two lines, led by a 13px icon naming the kind of reason:
- `affiliate`: a relation ("Sequel to X")
- `sparkles`: semantic similarity ("Feels like X")
- `feather`: same author ("By an author you read")
- `users`: the crowd ("Readers like you also finished this")
- `heart`: taste match ("Close to your taste")

Then up to two matched tags as `engine-chip`s (`surface-2`, `ink-3`, `badge-text` size). Tags go before genres: "Time Loop" says what a pick is, "Action" says what a third of the catalogue is. Two, because three truncate at this width. The tags are already spoiler-filtered.

**The consumer provides:** `items`, `seriesIdFor(item)`, `onOpen(item)`. Use it for Discover's "based on your recent activity", the series page's "more like this" and Home's "you might like". Catalogue rankings stay on `DiscoverRail`.
