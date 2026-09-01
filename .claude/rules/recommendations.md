---
paths:
  - "src/Maki.Api/Services/*Recommendation*.cs"
  - "src/Maki.Api/Services/*Rail*.cs"
  - "src/Maki.Api/Services/*Discover*.cs"
  - "src/Maki.Api/Services/*Taste*.cs"
  - "src/Maki.Api/Services/SeedWeight*.cs"
  - "src/Maki.Api/Controllers/Recommendation*.cs"
  - "src/Maki.Api/Controllers/Settings*.cs"
  - "src/Maki.Metadata/Taste/**"
  - "src/Maki.Core/Recommendations/**"
  - "distribution/**"
---

# Recommendations, taste, home layout

Migrated out of the root CLAUDE.md so this only loads when touching recommendation/discover/home-layout code.

- **The behavioural channel is a second vector space, not a third graph** (`Maki.Metadata.Taste`, `{ConfigDir}/taste-vectors.db`, kill switch `recommendations.tastevectors`). Item vectors factorized from AniList reading lists, scored as a cosine beside the text one in the same row pass. It reaches ~95k of the ~126k indexed rows against the co-read graph's ~41k, which is the point: a pair table is a lookup and is empty for most of the catalogue. `MangaBakaRecommendation.TasteMatch` is its own "why" and must not be folded into `CoRead` — they answer different questions off the same file, and switching co-read off still costs measurable relevance.
- **`VectorIndexCache` loads the taste artifact, so installing it invalidates the whole index** rather than swapping a file in the way the graph caches do. The scan needs a vector per row, and a dictionary lookup per candidate per query would cost more than the dot product it feeds. That is why `TasteVectorJob` runs last of the four staggered artifact downloads: doing it while the index is still building the first time throws that work away.
- **`Maki.Core.Recommendations.TasteTuning` and `Maki.Metadata.Taste.TasteVectorTuning` are unrelated.** The first is seed *weighting* (how much each of a reader's series steers the profile); the second is the behavioural *channel*. Any file using both needs one of them qualified, and `RecommendationService`'s local is `tasteVectors` because its constructor parameter `taste` is `BehavioralTasteService`.
- **Anything measured only against the crowd pair graphs is suspect in two specific directions** (details and numbers in `distribution/CLAUDE.md`): they over-represent famous titles, and they under-represent *affinity* — same creator, same franchise. A change that suppresses an affinity relationship looks free there and costs real relevance against held-out reading lists. Both biases surface in the eval's `pop` column before they surface anywhere else.
- **Discover's "Based on your recent activity" rail is the one per-user rail on that page** (`RecentActivityRailService`, `GET recommendations/discover/recent`), fetched separately from `GET discover` because those rails are cached once instance-wide with no viewer in scope. It only narrows the seeds — scoring, the owned-series exclusion, the rating clamp and the 12-hour pool cache all stay `RecommendationService`'s, which is also why it costs nothing on a warm pool. Seeds are the 8 most recently read series by `ReadCounts.ReadFor`; `Incognito.Full` is excluded by hand here (its `ChapterProgress` rows exist, and the subtitle names the titles). Its `Feed` is not a `BrowseFeed` — the client branches on `DiscoverRail.SeedIds` to page the recommender instead of `GetFeedAsync`, which rejects the name.
- **Home sections are user-ordered** (`ui.homesections`, `HomeLayoutSpec`). `Merge` runs on every read/write: unknown keys dropped, new keys appended enabled (never re-slotted) so a release adding a section doesn't scramble the user's ordering.
- **Home's "recently added" rail reads `ChapterFile.DateAdded`**, not `StatsEvents` (which is aggregated to one row/series/day — can't name the newest chapter). Both reading rails do a bounded scan + in-memory group, not an unbounded `GROUP BY`, since after a Kavita import that'd aggregate the whole library on every page load.
