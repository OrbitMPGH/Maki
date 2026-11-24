# EngineCard

The poster card for rows the recommender produced (`EngineRailRow` of `EngineCard`s): the catalogue card's poster plus a footer that says why this pick is here.

The wider cards leave room for the reason below the poster. Discover's desktop widths are 180px, 226px and 282px at compact, default and comfortable densities. On phones the rail shows two full cards and part of the next.

The footer puts a short reason on `surface` below the poster. A reason tied to a named seed can show its thumbnail and title. If every card shares the same seed and reason, the rail shows that reason once above the covers.

Up to three spoiler-filtered matches go in one line of small `TagChip`s. Tags come before genres and only whole chips show. The footer can hold just that line when the rail already names the reason.

**The consumer provides:** `items`, `seriesIdFor(item)`, `onOpen(item)`. Use it for Discover's "based on your recent activity", the series page's "more like this" and Home's "you might like". Catalogue rankings stay on `DiscoverRail`.
